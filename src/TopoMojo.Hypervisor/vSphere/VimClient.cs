// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a 3 Clause BSD-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VimClient;
using TopoMojo.Hypervisor.Extensions;

namespace TopoMojo.Hypervisor.vSphere
{
    public partial class VimClient
    {
        public VimClient(
            HypervisorServiceConfiguration options,
            ConcurrentDictionary<string, Vm> vmCache,
            VlanManager networkManager,
            ILogger<VimClient> logger
        )
        {
            _logger = logger;
            _config = options;
            _logger.LogDebug("Constructing, Client {host}", _config.Host);
            _tasks = [];
            _vmCache = vmCache;
            _vlanman = networkManager;
            _hostPrefix = _config.Host.Split('.').FirstOrDefault();
            _ = MonitorSession();
            _ = MonitorTasks();
            _config.Tenant ??= "";
        }

        private readonly VlanManager _vlanman;
        private readonly ILogger<VimClient> _logger;
        readonly Dictionary<string, VimHostTask> _tasks;
        private readonly ConcurrentDictionary<string, Vm> _vmCache;
        readonly Dictionary<string, TaskInfo> _taskMap = [];
        readonly ConcurrentDictionary<string, string> _dsnsMap = new();
        private INetworkManager _netman;
        readonly HypervisorServiceConfiguration _config = null;
        VimPortTypeClient _vim = null;
        ServiceContent _sic = null;
        ManagedObjectReference _props, _vdm, _file;
        ManagedObjectReference _datacenter, _dsns, _vms, _res, _pool;
        string _dvsuuid = "";
        readonly int _pollInterval = 1000;
        readonly int _syncInterval = 30000;
        readonly int _taskMonitorInterval = 3000;
        readonly string _hostPrefix = "";
        DateTimeOffset _lastAction;

        public string Name
        {
            get { return _config.Host; }
        }

        public HypervisorServiceConfiguration Options
        {
            get { return _config; }
        }

        public async Task<Vm[]> Find(string term)
        {
            await Connect();
            Vm[] list = await ReloadVmCache();
            if (term.HasValue())
                return list.Where(o => o.Id.Contains(term) || o.Name.Contains(term)).ToArray();
            return list;
        }

        public async Task<Vm> Start(string id)
        {
            await Connect();
            Vm vm = _vmCache[id];
            _logger.LogDebug("Starting vm {name}", vm.Name);
            if (vm.State != VmPowerState.Running)
            {
                ManagedObjectReference task = await _vim.PowerOnVM_TaskAsync(vm.AsVim(), null);
                TaskInfo info = await WaitForVimTask(task);
                if (info.state == TaskInfoState.success || (info.error?.localizedMessage.Contains("powered on", StringComparison.CurrentCultureIgnoreCase) ?? false))
                    vm.State = VmPowerState.Running;
                else
                    throw new Exception(info.error.localizedMessage);

                //apply guestinfo for annotations
                await ReconfigureVm(id, "guest", "", "");

            }

            _vmCache.TryUpdate(vm.Id, vm, vm);
            return vm;
        }

        public async Task<Vm> Stop(string id)
        {
            await Connect();

            Vm vm = _vmCache[id];

            _logger.LogDebug("Stopping vm {name}", vm.Name);

            if (vm.State == VmPowerState.Running)
            {
                ManagedObjectReference task = await _vim.PowerOffVM_TaskAsync(vm.AsVim());

                TaskInfo info = await WaitForVimTask(task);

                if (info.state == TaskInfoState.success || (info.error?.localizedMessage.Contains("powered off", StringComparison.CurrentCultureIgnoreCase) ?? false))
                    vm.State = VmPowerState.Off;
                else
                    throw new Exception(info.error.localizedMessage);

                _vmCache.TryUpdate(vm.Id, vm, vm);
            }

            return vm;
        }

        public async Task<Vm> Save(string id)
        {
            await Connect();
            Vm vm = _vmCache[id];

            //protect stock disks; only save a disk if it is local to the workspace
            //i.e. the disk folder matches the workspaceId
            if (vm.Name.Tag().HasValue() && !vm.DiskPath.Contains(vm.Name.Tag()))
                throw new InvalidOperationException("External templates must be cloned into local templates in order to be saved.");

            _logger.LogDebug("Save: get current snap for vm {name}", vm.Name);
            RetrievePropertiesResponse response = await _vim.RetrievePropertiesAsync(
                _props,
                FilterFactory.VmFilter(vm.AsVim(), "snapshot"));
            ObjectContent[] oc = response.returnval;
            //Get the current snapshot mor
            ManagedObjectReference mor = ((VirtualMachineSnapshotInfo)oc.First().GetProperty("snapshot")).currentSnapshot;

            //add new snapshot
            _logger.LogDebug("Save: add new snap for vm {name}", vm.Name);
            ManagedObjectReference task = await _vim.CreateSnapshot_TaskAsync(
                vm.AsVim(),
                "Root Snap",
                "Created by TopoMojo Save at " + DateTimeOffset.UtcNow.ToString("s") + "Z",
                false, false);
            await WaitForVimTask(task);

            //remove previous snapshot
            if (mor != null)
            {
                _logger.LogDebug("Save: remove previous snap for vm {name}", vm.Name);
                task = await _vim.RemoveSnapshot_TaskAsync(mor, false, true);

                await Task.Delay(500);

                TaskInfo info = await GetVimTaskInfo(task);
                if (info.state == TaskInfoState.error)
                    throw new Exception(info.error.localizedMessage);

                if (info.progress < 100)
                {
                    var t = new VimHostTask { Task = task, Action = "saving", WhenCreated = DateTimeOffset.UtcNow };
                    vm.Task = new VmTask { Name = t.Action, WhenCreated = t.WhenCreated, Progress = t.Progress };
                    _tasks.Add(vm.Id, t);
                }
            }
            _vmCache.TryUpdate(vm.Id, vm, vm);
            return vm;
        }

        public async Task<Vm> Revert(string id)
        {
            await Connect();
            Vm vm = _vmCache[id];
            _logger.LogDebug("Stopping vm {name}", vm.Name);
            ManagedObjectReference task = await _vim.RevertToCurrentSnapshot_TaskAsync(
                vm.AsVim(), null, false);
            await WaitForVimTask(task);
            if (vm.State == VmPowerState.Running)
                await Start(id);
            _vmCache.TryUpdate(vm.Id, vm, vm);
            return vm;
        }

        public async Task<Vm> Delete(string id)
        {
            //Implemented by stopping vm (if necessary), unregistering vm, and deleting vm folder
            //This protects the base disk from deletion.  When we get vvols, and a data provider
            //with instance-clone of vvols, every vm will have its own disk, and we can just
            //delete the vm.
            await Connect();
            Vm vm = _vmCache[id];
            // string tag = vm.Name.Tag();

            _logger.LogDebug("Delete: stopping vm {name}", vm.Name);
            await Stop(id);

            _logger.LogDebug("Delete: unregistering vm {name}", vm.Name);
            await _netman.Unprovision(vm.AsVim());
            await _vim.UnregisterVMAsync(vm.AsVim());

            string folder = vm.Path[..vm.Path.LastIndexOf('/')];
            _logger.LogDebug("Delete: deleting vm folder {folder}", folder);
            await _vim.DeleteDatastoreFile_TaskAsync(_sic.fileManager, folder, _datacenter);

            if (!_vmCache.TryRemove(vm.Id, out _))
            {
                await Task.Delay(100);
                _vmCache.TryRemove(vm.Id, out _);
            }

            vm.Status = "initialized";
            _logger.LogDebug("Delete: delete complete {id}", id);

            return vm;
        }

        public async Task<string> GetTicket(string id)
        {
            await Connect();
            Vm vm = _vmCache[id];
            _logger.LogDebug("Aquiring mks ticket for vm {name}", vm.Name);
            var ticket = await _vim.AcquireTicketAsync(vm.AsVim(), "webmks");
            string port = (ticket.portSpecified && ticket.port != 443) ? $":{ticket.port}" : "";
            return $"wss://{ticket.host ?? _config.Host}{port}/ticket/{ticket.ticket}";
        }

        public async Task<Vm> Deploy(VmTemplate template)
        {
            await Connect();

            _logger.LogDebug("deploy: validate portgroups...");
            await _netman.Provision(template);

            _logger.LogDebug("deploy: transform template...");
            //var transformer = new VCenterTransformer { DVSuuid = _dvsuuid };
            VirtualMachineConfigSpec vmcs = Transform.TemplateToVmSpec(
                template,
                _config.VmStore.Replace("{host}", _hostPrefix),
                _dvsuuid
            );

            _logger.LogDebug("deploy: create vm...");
            ManagedObjectReference task = await _vim.CreateVM_TaskAsync(_vms, vmcs, _pool, null);
            TaskInfo info = await WaitForVimTask(task);
            Vm vm;
            if (info.state == TaskInfoState.success)
            {
                _logger.LogDebug("deploy: load vm...");
                await Task.Delay(200);
                vm = await GetVirtualMachine((ManagedObjectReference)info.result);

                _logger.LogDebug("deploy: create snapshot...");
                task = await _vim.CreateSnapshot_TaskAsync(
                    vm.AsVim(),
                    "Root Snap",
                    "Created by TopoMojo Deploy at " + DateTimeOffset.UtcNow.ToString("s") + "Z",
                    false, false);
                info = await WaitForVimTask(task);
                if (template.AutoStart && info.state == TaskInfoState.success)
                {
                    _logger.LogDebug("deploy: start vm...");
                    vm = await Start(vm.Id);
                }
            }
            else
            {
                throw new Exception(info.error.localizedMessage);
            }
            return vm;
        }

        public async Task SetAffinity(string isolationTag, Vm[] vms, bool start)
        {
            if (_config.IsVCenter)
            {
                var configSpec = new ClusterConfigSpec();
                var affinityRuleSpec = new ClusterAffinityRuleSpec();
                var clusterRuleSpec = new ClusterRuleSpec();

                affinityRuleSpec.vm = vms.Select(m => m.Reference.AsReference()).ToArray();
                affinityRuleSpec.name = $"Affinity#{isolationTag}";
                affinityRuleSpec.enabled = true;
                affinityRuleSpec.enabledSpecified = true;
                affinityRuleSpec.mandatory = true;
                affinityRuleSpec.mandatorySpecified = true;

                clusterRuleSpec.operation = ArrayUpdateOperation.add;
                clusterRuleSpec.info = affinityRuleSpec;

                configSpec.rulesSpec = [clusterRuleSpec];
                _logger.LogDebug("setaffinity: reconfiguring cluster ");
                await _vim.ReconfigureCluster_TaskAsync(_res, configSpec, true);
            }

            if (start)
                await Task.WhenAll(vms.Select(vm => Start(vm.Id)));
        }

        public async Task<Vm> Change(string id, VmKeyValue change)
        {
            var segments = change.Value.Split(':');

            string label = segments.Length > 1
                ? segments.Last()
                : "";

            return await ReconfigureVm(id, change.Key, label, segments.First());
        }

        //id, feature (iso, net, boot, guest), label, value
        public async Task<Vm> ReconfigureVm(string id, string feature, string label, string newvalue)
        {
            await Connect();

            if (int.TryParse(label, out int index))
                label = "";

            Vm vm = _vmCache[id];
            RetrievePropertiesResponse response = await _vim.RetrievePropertiesAsync(
                _props,
                FilterFactory.VmFilter(vm.AsVim(), "config"));
            ObjectContent[] oc = response.returnval;

            VirtualMachineConfigInfo config = (VirtualMachineConfigInfo)oc[0].GetProperty("config");
            VirtualMachineConfigSpec vmcs = new();

            switch (feature)
            {
                case "iso":
                    VirtualCdrom cdrom = (VirtualCdrom)(label.HasValue()
                        ? config.hardware.device.Where(o => o.deviceInfo.label == label).SingleOrDefault()
                        : config.hardware.device.OfType<VirtualCdrom>().ToArray()[index]);

                    if (cdrom != null)
                    {
                        if (cdrom.backing.GetType() != typeof(VirtualCdromIsoBackingInfo))
                            cdrom.backing = new VirtualCdromIsoBackingInfo();

                        ((VirtualCdromIsoBackingInfo)cdrom.backing).fileName = newvalue;
                        cdrom.connectable = new VirtualDeviceConnectInfo
                        {
                            connected = true,
                            startConnected = true
                        };

                        vmcs.deviceChange = [
                            new VirtualDeviceConfigSpec
                            {
                                device = cdrom,
                                operation = VirtualDeviceConfigSpecOperation.edit,
                                operationSpecified = true
                            }
                        ];
                    }
                    break;

                case "net":
                case "eth":
                    VirtualEthernetCard card = (VirtualEthernetCard)(label.HasValue()
                        ? config.hardware.device.Where(o => o.deviceInfo.label == label).SingleOrDefault()
                        : config.hardware.device.OfType<VirtualEthernetCard>().ToArray()[index]);

                    if (card != null)
                    {
                        if (newvalue.StartsWith("_none_"))
                        {
                            card.connectable = new VirtualDeviceConnectInfo()
                            {
                                connected = false,
                                startConnected = false,
                            };
                        }
                        else
                        {
                            _netman.UpdateEthernetCardBacking(card, newvalue);
                            card.connectable.connected = true;
                        }

                        vmcs.deviceChange = [
                            new VirtualDeviceConfigSpec
                            {
                                device = card,
                                operation = VirtualDeviceConfigSpecOperation.edit,
                                operationSpecified = true
                            }
                        ];
                    }
                    break;

                case "boot":
                    int delay = 0;
                    if (int.TryParse(newvalue, out delay))
                        vmcs.AddBootOption(delay);
                    break;

                case "guest":
                    if (newvalue.HasValue() && !newvalue.EndsWith('\n'))
                        newvalue += '\n';
                    vmcs.annotation = config.annotation + newvalue;
                    if (vm.State == VmPowerState.Running && vmcs.annotation.HasValue())
                        vmcs.AddGuestInfo(EndOfLineRegex().Split(vmcs.annotation));
                    break;

                default:
                    throw new Exception("Invalid change request.");
                    //break;
            }

            ManagedObjectReference task = await _vim.ReconfigVM_TaskAsync(vm.AsVim(), vmcs);
            TaskInfo info = await WaitForVimTask(task);
            if (info.state == TaskInfoState.error)
                throw new Exception(info.error.localizedMessage);
            return await GetVirtualMachine(vm.AsVim());
        }

        public async Task<int> TaskProgress(string id)
        {
            await Connect();

            // no task
            if (_taskMap.ContainsKey(id).Equals(false))
                return -1;

            // initialized task
            if (_taskMap[id] is null)
                return 0;

            int progress = 0;
            int taskprogress = 0;
            string state = "";
            string msg = "";

            // active task
            try
            {
                _taskMap[id] = await GetVimTaskInfo(_taskMap[id].task);
                state = _taskMap[id].state.ToString();
                taskprogress = _taskMap[id].progress;

                switch (_taskMap[id].state)
                {
                    case TaskInfoState.error:
                        progress = 100;
                        msg = string.Format("{0} - {1}",
                            _taskMap[id].description.message,
                            _taskMap[id].error.localizedMessage
                        );
                        break;

                    case TaskInfoState.success:
                        progress = 100;
                        break;

                    default:
                        progress = _taskMap[id].progress;
                        break;
                }
            }
            catch
            {
                progress = 100;
            }
            finally
            {
                if (progress == 100)
                    _taskMap.Remove(id);
            }

            _logger.LogDebug("TaskProgress: {id} {state} {taskprogress}% resolved:{progress}% {msg}",
                id,
                state,
                taskprogress,
                progress,
                msg
            );

            return progress;
        }

        public async Task<bool> FolderExists(string path)
        {
            string[] files = await GetFiles(path + "/*", false);
            return files.Length > 0;
        }

        public async Task<bool> FileExists(string path)
        {
            string[] list = await GetFiles(path, false);
            return list.Any(x => x == path);
        }

        public async Task<string[]> GetFiles(string path, bool recursive)
        {
            await Connect();
            List<string> list = [];
            DatastorePath dsPath = new(path);
            string oldRoot = "";
            string pattern = dsPath.File ?? "*";

            RetrievePropertiesResponse response = await _vim.RetrievePropertiesAsync(
                _props,
                FilterFactory.DatastoreFilter(_res)
            );

            ObjectContent[] oc = response.returnval;

            foreach (ObjectContent obj in oc)
            {
                ManagedObjectReference dsBrowser = (ManagedObjectReference)obj.propSet[0].val;

                var capability = obj.propSet[1].val as DatastoreCapability;

                var summary = obj.propSet[2].val as DatastoreSummary;

                if (summary.name == dsPath.Datastore)
                {
                    // if topLevelDirectory not supported (vsan), map from directory name to guid)
                    if (
                        capability.topLevelDirectoryCreateSupportedSpecified
                        && !capability.topLevelDirectoryCreateSupported
                        && dsPath.TopLevelFolder.HasValue()
                    )
                    {
                        try
                        {
                            oldRoot = dsPath.TopLevelFolder;
                            string target = summary.url + oldRoot;

                            if (!_dsnsMap.ContainsKey(target))
                            {
                                var result = await _vim.ConvertNamespacePathToUuidPathAsync(
                                    _dsns,
                                    _datacenter,
                                    target
                                );

                                _dsnsMap.TryAdd(target, result.Replace(summary.url, ""));
                            }

                            dsPath.TopLevelFolder = _dsnsMap[target];

                            // vmcloud sddc errors on Search_Datastore()
                            // so force SearchDatastoreSubFolders()
                            recursive = true;
                            pattern = "*" + Path.GetExtension(dsPath.File);

                            _logger.LogDebug("mapped datastore namespace: {path}", dsPath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "error processing vsan toplevel folder");
                        }
                    }

                    ManagedObjectReference task = null;
                    TaskInfo info = null;

                    var spec = new HostDatastoreBrowserSearchSpec
                    {
                        matchPattern = [pattern],
                    };

                    var results = new List<HostDatastoreBrowserSearchResults>();

                    if (recursive)
                    {
                        try
                        {

                            if (_config.DebugVerbose)
                                _logger.LogDebug("searching recursive {path} for {pattern}", dsPath.FolderPath, pattern);

                            task = await _vim.SearchDatastoreSubFolders_TaskAsync(
                                dsBrowser, dsPath.FolderPath, spec
                            );

                            info = await WaitForVimTask(task);

                            if (_config.DebugVerbose)
                                _logger.LogDebug("searching recursive {path} for {pattern}; found [{count}]",
                                    dsPath.FolderPath,
                                    pattern,
                                    ((HostDatastoreBrowserSearchResults[])info.result)?.Length ?? 0
                                );

                            if (info.result != null)
                                results.AddRange((HostDatastoreBrowserSearchResults[])info.result);

                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "error searching datastore.");
                        }
                    }
                    else
                    {
                        if (_config.DebugVerbose)
                            _logger.LogDebug("searching {path} for {pattern}", dsPath.FolderPath, pattern);

                        task = await _vim.SearchDatastore_TaskAsync(
                            dsBrowser, dsPath.FolderPath, spec
                        );

                        info = await WaitForVimTask(task);

                        if (_config.DebugVerbose)
                            _logger.LogDebug("searching {path} for {pattern}; found [{count}]",
                                dsPath.FolderPath,
                                pattern,
                                ((HostDatastoreBrowserSearchResults[])info.result)?.Length ?? 0
                            );

                        if (info.result != null)
                            results.Add((HostDatastoreBrowserSearchResults)info.result);
                    }

                    try
                    {
                        foreach (HostDatastoreBrowserSearchResults result in results)
                        {
                            if (result != null && result.file != null && result.file.Length > 0)
                            {
                                string fp = result.folderPath;

                                if (oldRoot.HasValue())
                                    fp = fp.Replace(dsPath.TopLevelFolder, oldRoot);

                                if (!fp.EndsWith('/'))
                                    fp += "/";

                                list.AddRange(result.file.Select(o => fp + o.path));

                                if (_config.DebugVerbose)
                                {
                                    foreach (var s in list)
                                        _logger.LogDebug("added file result {s}", s);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "error processing datastore search results.");
                    }
                }
            }

            if (_config.DebugVerbose)
                _logger.LogDebug("search datastore found [{count}] results.", list.Count);

            return [.. list];
        }

        public async Task CloneDisk(string source, string dest)
        {
            string pattern = @"blank-(\d+)([^\.]+)";

            await Connect();

            // register task for monitoring
            _taskMap.Add(dest, null);

            await MakeDirectories(dest);

            Match match = Regex.Match(source, pattern);

            ManagedObjectReference task;
            if (match.Success)
            {
                //create virtual disk
                if (!int.TryParse(match.Groups[1].Value, out int size))
                    size = 0;
                string[] parts = match.Groups[2].Value.Split('-');
                string adapter = (parts.Length > 1) ? parts[1] : "lsiLogic";
                FileBackedVirtualDiskSpec spec = new()
                {
                    diskType = "thin",
                    adapterType = adapter.Replace("lsilogic", "lsiLogic").Replace("buslogic", "busLogic"),
                    capacityKb = size * 1024 * 1024
                };
                _logger.LogDebug("creating new blank disk {dest}", dest);
                task = await _vim.CreateVirtualDisk_TaskAsync(
                    _vdm, dest, _datacenter, spec);
            }
            else
            {
                //copy virtual disk
                _logger.LogDebug("cloning new disk {source} -> {dest}", source, dest);
                task = await _vim.CopyVirtualDisk_TaskAsync(
                    _vdm, source, _datacenter, dest, _datacenter, null, false);
            }

            // sometimes returns blank info, so wait a sec to prevent race
            await Task.Delay(1000);

            _taskMap[dest] = await GetVimTaskInfo(task);
        }

        public async Task CreateDisk(string name, string type, string adapter, int size)
        {
            await Connect();
            _ = _vim.CreateVirtualDisk_TaskAsync(
                _vdm,
                name,
                _datacenter,
                new FileBackedVirtualDiskSpec
                {
                    diskType = type,
                    adapterType = adapter,
                    capacityKb = size * 1000 * 1000,
                    profile = null
                }
            );
        }

        public async Task CreateDisk(VmDisk disk)
        {
            await Connect();
            await MakeDirectories(disk.Path);

            string adapter = disk.Controller.HasValue()
                ? disk.Controller.Replace("lsilogic", "lsiLogic").Replace("buslogic", "busLogic")
                : "lsiLogic";
            var task = await _vim.CreateVirtualDisk_TaskAsync(
                _vdm,
                disk.Path,
                _datacenter,
                new FileBackedVirtualDiskSpec
                {
                    diskType = "thin",
                    adapterType = adapter,
                    capacityKb = disk.Size * 1024 * 1024,
                    profile = null
                }
            );
            await WaitForVimTask(task);
        }

        public async Task DeleteDisk(string path)
        {
            await Connect();
            _ = _vim.DeleteVirtualDisk_TaskAsync(_vdm, path, null);
        }

        public static async Task<string[]> GetGuestIds()
        {
            await Task.Delay(0);
            return Transform.OsMap;
        }

        public async Task<Vm> AnswerVmQuestion(string id, string question, string answer)
        {
            await Connect();
            Vm vm = _vmCache[id];
            await _vim.AnswerVMAsync(vm.AsVim(), question, answer);
            vm.Question = null;
            return vm;
        }

        private async Task MakeDirectories(string path)
        {
            try
            {
                if (!FolderExists(path).Result)
                    await _vim.MakeDirectoryAsync(_file, new DatastorePath(path).FolderPath, _datacenter, true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("MakeDirectories: {path} {msg}", path, ex.Message);
            }
        }


        private async Task<TaskInfo> WaitForVimTask(ManagedObjectReference task)
        {
            int i = 0;
            TaskInfo info;

            //iterate the search until complete or timeout occurs
            do
            {
                //check every so often
                await Task.Delay(_pollInterval);

                info = await GetVimTaskInfo(task);

                //increment timeout counter
                i++;
                //_idle = 0;

                if (_config.DebugVerbose)
                    _logger.LogDebug("waiting for vim task ({value})...state = {state}", task.Value, info.state);

                //check for status updates until the task is complete
            } while (info.state == TaskInfoState.running || info.state == TaskInfoState.queued);

            //return the task info
            return info;
        }

        private async Task<TaskInfo> GetVimTaskInfo(ManagedObjectReference task)
        {
            await Connect();

            TaskInfo info;

            try
            {
                RetrievePropertiesResponse response = await _vim.RetrievePropertiesAsync(
                    _props,
                    FilterFactory.TaskFilter(task)
                );

                ObjectContent[] oc = response.returnval;

                info = (TaskInfo)oc[0]?.propSet[0]?.val;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get TaskInfo for {value}", task.Value);

                info = new TaskInfo
                {
                    task = task,
                    state = TaskInfoState.error
                };
            }

            return info;
        }

        private Task Connect()
        {
            _lastAction = DateTimeOffset.UtcNow;

            if (_vim != null && _vim.State == CommunicationState.Opened)
                return Task.CompletedTask;

            //only want one client object created, so first one through wins
            //everyone else wait here
            lock (_config)
            {
                if (_vim != null && _vim.State == CommunicationState.Faulted)
                {
                    _logger.LogDebug("{url} CommunicationState is Faulted.", _config.Url);
                    Disconnect().Wait();
                }

                if (_vim == null)
                {
                    try
                    {
                        DateTimeOffset sp = DateTimeOffset.Now;
                        _logger.LogDebug("Instantiating client {host}...", _config.Host);
                        VimPortTypeClient client = new(VimPortTypeClient.EndpointConfiguration.VimPort, _config.Url);

                        if (_config.IgnoreCertificateErrors)
                        {
                            client.ClientCredentials.ServiceCertificate.SslCertificateAuthentication =
                                new System.ServiceModel.Security.X509ServiceCertificateAuthentication()
                                {
                                    CertificateValidationMode = System.ServiceModel.Security.X509CertificateValidationMode.None,
                                    RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
                                }
                            ;
                        }

                        _logger.LogDebug("client: [{client}]", client);
                        _logger.LogDebug("Instantiated {host} in {time}s", _config.Host, DateTimeOffset.Now.Subtract(sp).TotalSeconds);

                        sp = DateTimeOffset.Now;
                        _logger.LogInformation("Connecting to {url}...", _config.Url);
                        _sic = client.RetrieveServiceContentAsync(new ManagedObjectReference { type = "ServiceInstance", Value = "ServiceInstance" }).Result;

                        if (_sic is null)
                            throw new Exception("Failed to retrieve ServiceContent from vmware sdk.");

                        _config.IsVCenter = _sic.about?.apiType == "VirtualCenter";
                        _props = _sic.propertyCollector;
                        _vdm = _sic.virtualDiskManager;
                        _file = _sic.fileManager;
                        _dsns = _sic.datastoreNamespaceManager;

                        _logger.LogDebug("Connected {host} in {time}s", _config.Host, DateTimeOffset.Now.Subtract(sp).TotalSeconds);

                        sp = DateTimeOffset.Now;
                        _logger.LogInformation("Authenticating {host} {user}", _config.Host, _config.User);
                        _ = client.LoginAsync(_sic.sessionManager, _config.User, _config.Password, null).Result;
                        _logger.LogDebug("Authenticated {host} in {time}s", _config.Host, DateTimeOffset.Now.Subtract(sp).TotalSeconds);

                        sp = DateTimeOffset.Now;
                        _logger.LogDebug("Initializing {host}...", _config.Host);
                        InitReferences(client).Wait();
                        _logger.LogDebug("Initialized {host} in {time}s", _config.Host, DateTimeOffset.Now.Subtract(sp).TotalSeconds);

                        _vim = client;

                        sp = DateTimeOffset.Now;
                        _logger.LogDebug("Loading {host}...", _config.Host);
                        ReloadVmCache().Wait();
                        _netman.Clean().Wait();
                        _logger.LogDebug("Loaded {host} in {time}s", _config.Host, DateTimeOffset.Now.Subtract(sp).TotalSeconds);

                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(0, ex, "Failed to connect with {url}", _config.Url);
                    }
                }
            }

            return Task.CompletedTask;
        }

        public async Task Disconnect()
        {
            _logger.LogDebug("Disconnecting from {name}", Name);
            await Task.Delay(500);
            _vim.Dispose();
            _vim = null;
            _sic = null;
        }

        private async Task<ObjectContent[]> LoadReferenceTree(VimPortTypeClient client)
        {
            var plan = new TraversalSpec
            {
                name = "FolderTraverseSpec",
                type = "Folder",
                path = "childEntity",
                selectSet = [

                    new TraversalSpec()
                    {
                        type = "Datacenter",
                        path = "hostFolder",
                        selectSet = [
                            new SelectionSpec
                            {
                                name = "FolderTraverseSpec"
                            }
                        ]
                    },

                    new TraversalSpec()
                    {
                        type = "Datacenter",
                        path = "networkFolder",
                        selectSet = [
                            new SelectionSpec
                            {
                                name = "FolderTraverseSpec"
                            }
                        ]
                    },

                    new TraversalSpec()
                    {
                        type = "ComputeResource",
                        path = "resourcePool",
                        selectSet =
                        [
                            new TraversalSpec
                            {
                                type = "ResourcePool",
                                path = "resourcePool"
                            }
                        ]
                    },

                    new TraversalSpec()
                    {
                        type = "ComputeResource",
                        path = "host"
                    },

                    new TraversalSpec()
                    {
                        type = "Folder",
                        path = "childEntity"
                    }
                ]
            };

            var props = new PropertySpec[]
            {
                new() {
                    type = "Datacenter",
                    pathSet = ["name", "parent", "vmFolder"]
                },

                new() {
                    type = "ComputeResource",
                    pathSet = ["name", "parent", "resourcePool", "host"]
                },

                new() {
                    type = "HostSystem",
                    pathSet = ["configManager"]
                },

                new() {
                    type = "ResourcePool",
                    pathSet = ["name", "parent", "resourcePool"]
                },

                new() {
                    type = "DistributedVirtualSwitch",
                    pathSet = ["name", "parent", "uuid"]
                },

                new() {
                    type = "DistributedVirtualPortgroup",
                    pathSet = ["name", "parent", "config"]
                }

            };

            ObjectSpec objectspec = new()
            {
                obj = _sic.rootFolder,
                selectSet = [plan]
            };

            PropertyFilterSpec filter = new()
            {
                propSet = props,
                objectSet = [objectspec]
            };

            PropertyFilterSpec[] filters = [filter];
            RetrievePropertiesResponse response = await client.RetrievePropertiesAsync(_props, filters);

            return response.returnval;
        }

        private async Task InitReferences(VimPortTypeClient client)
        {
            var clunkyTree = await LoadReferenceTree(client);
            if (clunkyTree.Length == 0)
                throw new InvalidOperationException();

            string[] path = _config.PoolPath.ToLower().Split(['/', '\\']);
            string datacenter = (path.Length > 0) ? path[0] : "";
            string cluster = (path.Length > 1) ? path[1] : "";
            string pool = (path.Length > 2) ? path[2] : "";

            var dcContent = clunkyTree.FindTypeByName("Datacenter", datacenter) ?? clunkyTree.First("Datacenter");
            _datacenter = dcContent.obj;
            _vms = (ManagedObjectReference)dcContent.GetProperty("vmFolder");

            var clusterContent = clunkyTree.FindTypeByName("ComputeResource", cluster) ?? clunkyTree.First("ComputeResource");
            _res = clusterContent.obj;

            var poolContent = clunkyTree.FindTypeByName("ResourcePool", pool)
                ?? clunkyTree.FindTypeByReference(
                    (ManagedObjectReference)clusterContent.GetProperty("resourcePool")
                );
            _pool = poolContent.obj;

            var netSettings = new VimReferences
            {
                Vim = client,
                Cluster = _res,
                Props = _props,
                Pool = _pool,
                VmFolder = _vms,
                UplinkSwitch = _config.Uplink,
                ExcludeNetworkMask = _config.ExcludeNetworkMask,
                TenantId = _config.Tenant
            };

            if (_config.IsVCenter)
            {
                if (poolContent.GetProperty("resourcePool") is ManagedObjectReference[] subpools && subpools.Length > 0)
                    _pool = subpools.First();

                var dvs = clunkyTree.FindTypeByName("DistributedVirtualSwitch", _config.Uplink.ToLower()) ?? clunkyTree.First("DistributedVirtualSwitch");
                _dvsuuid = dvs?.GetProperty("uuid").ToString();
                netSettings.Dvs = dvs?.obj;
                netSettings.DvsUuid = _dvsuuid;

                if (_config.IsNsxNetwork || _config.Uplink.StartsWith("nsx."))
                {
                    _netman = new NsxNetworkManager(
                        _logger,
                        netSettings,
                        _vmCache,
                        _vlanman,
                        _config.Sddc
                    );
                }
                else
                {
                    _netman = new DistributedNetworkManager(
                        _logger,
                        netSettings,
                        _vmCache,
                        _vlanman
                    );
                }
            }
            else
            {
                var hostContent = clunkyTree.FindType("HostSystem").FirstOrDefault();
                if (hostContent != null)
                {
                    var hostConfig = hostContent.GetProperty("configManager") as HostConfigManager;
                    netSettings.Net = hostConfig?.networkSystem;
                }

                _netman = new HostNetworkManager(
                    _logger,
                    netSettings,
                    _vmCache,
                    _vlanman
                );
            }

            await _netman.Initialize();

        }

        private async Task<Vm[]> ReloadVmCache()
        {
            List<string> existing = _vmCache.Values
                .Where(v => v.Host == _config.Host)
                .Select(o => o.Id)
                .ToList();

            List<Vm> list = [];

            //retrieve the properties specificied
            RetrievePropertiesResponse response = await _vim.RetrievePropertiesAsync(
                _props,
                FilterFactory.VmFilter(_pool)); // _vms

            ObjectContent[] oc = response.returnval;

            //iterate through the collection of Vm's
            foreach (ObjectContent obj in oc)
            {
                Vm vm = LoadVm(obj);
                if (vm != null)
                    list.Add(vm);
            }

            List<string> active = list.Select(o => o.Id).ToList();
            _logger.LogDebug("refreshing cache [{host}] existing: {existing} active: {active}", _config.Host, existing.Count, active.Count);

            foreach (string key in existing.Except(active))
            {
                if (_vmCache.TryRemove(key, out Vm stale))
                {
                    _logger.LogDebug("removing stale cache entry [{host}] {stale}", _config.Host, stale.Name);
                }
            }

            //return an array of vm's
            return [.. list];
        }

        private Vm LoadVm(ObjectContent obj)
        {

            //create a new vm object
            Vm vm = new();

            //iterate through the retrieved properties and set values for the appropriate types
            foreach (DynamicProperty dp in obj.propSet)
            {
                if (dp.val.GetType() == typeof(VirtualMachineRuntimeInfo))
                {
                    VirtualMachineRuntimeInfo runtime = (VirtualMachineRuntimeInfo)dp.val;
                    //vm.Question = GetQuestion(runtime);
                }

                if (dp.val.GetType() == typeof(VirtualMachineSnapshotInfo))
                {
                }

                if (dp.val.GetType() == typeof(VirtualMachineFileLayout))
                {
                    VirtualMachineFileLayout layout = (VirtualMachineFileLayout)dp.val;
                    if (layout != null && layout.disk != null && layout.disk.Length > 0 && layout.disk[0].diskFile.Length > 0)
                    {
                        //_logger.LogDebug(layout.disk[0].diskFile[0]);
                        vm.DiskPath = layout.disk[0].diskFile[0];
                    }
                }

                if (dp.val.GetType() == typeof(VirtualMachineSummary))
                {
                    try
                    {
                        VirtualMachineSummary summary = (VirtualMachineSummary)dp.val;
                        vm.Host = _config.Host;
                        //vm.HostId = _config.Id;
                        vm.Name = summary.config.name;
                        vm.Path = summary.config.vmPathName;
                        vm.Id = summary.config.uuid;
                        //vm.IpAddress = summary.guest.ipAddress;
                        //vm.Os = summary.guest.guestId;
                        vm.State = (summary.runtime.powerState == VirtualMachinePowerState.poweredOn)
                            ? VmPowerState.Running
                            : VmPowerState.Off;

                        //vm.IsPoweredOn = (summary.runtime.powerState == VirtualMachinePowerState.poweredOn);
                        vm.Reference = summary.vm.AsString(); //summary.vm.type + "|" + summary.vm.Value;
                        vm.Stats = string.Format("{0} | mem-{1}% cpu-{2}%", summary.overallStatus,
                            Math.Round(summary.quickStats.guestMemoryUsage / (float)summary.runtime.maxMemoryUsage * 100, 0),
                            Math.Round(summary.quickStats.overallCpuUsage / (float)summary.runtime.maxCpuUsage * 100, 0));
                        //vm.Annotations = summary.config.annotation.Lines();
                        //vm.ContextNumbers = vm.Annotations.FindOne("context").Value();
                        //vm.ContextNames = vm.Annotations.FindOne("display").Value();
                        //vm.HasGuestAgent = (vm.Annotations.FindOne("guestagent").Value() == "true");
                        vm.Question = GetQuestion(summary.runtime.question);
                        vm.Status = "deployed";
                        if (_tasks.TryGetValue(vm.Id, out VimHostTask value))
                        {
                            var t = value;
                            vm.Task = new VmTask { Name = t.Action, WhenCreated = t.WhenCreated, Progress = t.Progress };
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("{msg}", ex.Message);
                        if (!string.IsNullOrEmpty(vm.Name))
                        {
                            _logger.LogDebug("Error refreshing VirtualMachine {name} on host {hostt}", vm.Name, _config.Host);
                        }
                        else
                        {
                            _logger.LogDebug("Error refreshing host {host}", _config.Host);
                        }

                        return null;
                    }
                }
            }

            if (!vm.Id.HasValue())
            {
                _logger.LogWarning("{name} found a vm without an Id", Name);
                return null;
            }

            if (vm.Name.Contains('#').Equals(false) || vm.Name.ToTenant() != _config.Tenant)
                return null;

            _vmCache.AddOrUpdate(vm.Id, vm, (k, v) => v = vm);

            return vm;
        }

        private static VmQuestion GetQuestion(VirtualMachineQuestionInfo question)
        {
            if (question == null)
                return null;

            return new VmQuestion
            {
                Id = question.id,
                Prompt = question.text,
                DefaultChoice = question.choice.choiceInfo[question.choice.defaultIndex].key,
                Choices = question.choice.choiceInfo.Select(x =>
                    new VmQuestionChoice
                    {
                        Key = x.key,
                        Label = x.label
                    })
                    .ToArray(),
            };
        }

        private async Task<Vm> GetVirtualMachine(ManagedObjectReference mor)
        {
            RetrievePropertiesResponse response = await _vim.RetrievePropertiesAsync(
                _props,
                FilterFactory.VmFilter(mor));
            ObjectContent[] oc = response.returnval;
            return (oc.Length > 0) ? LoadVm(oc[0]) : null;
        }

        private async Task MonitorSession()
        {
            _logger.LogDebug("{host}: starting cache loop", _config.Host);
            await Connect();
            int step = 0;

            while (true)
            {
                var st = DateTimeOffset.UtcNow;

                try
                {

                    if (_vim != null && DateTimeOffset.UtcNow.AddMinutes(-_config.KeepAliveMinutes).CompareTo(_lastAction) > 0)
                    {
                        await Disconnect();
                    }

                    if (_vim != null && _vim.State == CommunicationState.Opened)
                    {
                        await ReloadVmCache();
                        if (step == 0)
                        {
                            await _netman.Clean();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(0, ex, "Failed to refresh cache for {host}", _config.Host);

                    if (ex.GetType().Name.Contains("ServerTooBusy"))
                        await Disconnect();
                }
                finally
                {
                    _logger.LogDebug("MonitorSession complete {duration}", DateTimeOffset.UtcNow.Subtract(st).TotalSeconds);

                    await Task.Delay(_syncInterval);

                    if (_vim == null)
                        await Connect();
                }

                step = (step + 1) % 2;
            }
        }

        private async Task MonitorTasks()
        {
            _logger.LogDebug("{host}: starting task monitor", _config.Host);
            while (true)
            {
                try
                {
                    foreach (string key in _tasks.Keys.ToArray())
                    {
                        var t = _tasks[key];
                        var info = await GetVimTaskInfo(t.Task);
                        Console.WriteLine("task progress: {0}", info.progress);
                        switch (info.state)
                        {
                            case TaskInfoState.error:
                                t.Progress = -1;
                                t.Action = info.description?.message + " - " +
                                    info.error?.localizedMessage;
                                _tasks.Remove(key);
                                break;

                            case TaskInfoState.success:
                                t.Progress = 100;
                                _tasks.Remove(key);
                                break;

                            default:
                                t.Progress = info.progress;
                                break;
                        }
                        if (_vmCache.TryGetValue(key, out Vm value))
                        {
                            Vm vm = value;
                            vm.Task ??= new VmTask();
                            vm.Task.Progress = t.Progress;
                            vm.Task.Name = t.Action;
                            _vmCache.TryUpdate(key, vm, vm);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(0, ex, "Error in task monitor of {host}", _config.Host);
                }
                finally
                {
                    await Task.Delay(_taskMonitorInterval);
                }
            }
        }

        internal async Task PreDeployNets(VmNet[] eths, bool useUplinkSwitch)
        {
            await _netman.ProvisionAll(eths, useUplinkSwitch);
        }

        [GeneratedRegex("\r\n|\r|\n")]
        private static partial Regex EndOfLineRegex();
    }

}
