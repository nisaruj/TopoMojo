// Copyright 2025 Carnegie Mellon University. All Rights Reserved.
// Released under a 3 Clause BSD-style license. See LICENSE.md in the project root for license information.

using AutoMapper;
using TopoMojo.Api.Models;

namespace TopoMojo.Api
{
    public class GamespaceProfile : Profile
    {
        public GamespaceProfile()
        {
            CreateMap<Data.Gamespace, Gamespace>()
            ;

            CreateMap<ChangedGamespace, Data.Gamespace>()
            ;

            CreateMap<Data.Gamespace, GameState>()
                .ForMember(d => d.Name, opt => opt.MapFrom(s => s.Workspace.Name))
                .ForMember(d => d.Challenge, opt => opt.Ignore())
            ;

            CreateMap<Data.Gamespace, GameStateSummary>()
            ;

            CreateMap<GameStateSummary, GameState>()
            ;

            CreateMap<Data.Player, Player>()
            ;

            CreateMap<ChallengeSpec, ChallengeView>()
            ;

            CreateMap<SectionSpec, SectionView>()
            ;

            CreateMap<QuestionSpec, QuestionView>()
                .ForMember(d => d.Answer, opt => opt.Ignore())
                .AfterMap((source, destination) =>
                {
                    // if they've solved this question, do include the
                    // answer for display purposes
                    if (source.IsCorrect && source.IsGraded)
                    {
                        destination.Answer = source.Answer;
                    }
                })
            ;

            CreateMap<VariantSpec, VariantView>()
                .ForMember(d => d.TotalSectionCount, opt => opt.MapFrom(s => s.Sections.Count));
        }
    }
}
