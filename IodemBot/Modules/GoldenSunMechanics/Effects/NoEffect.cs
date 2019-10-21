﻿using IodemBot.Modules.ColossoBattles;
using System.Collections.Generic;

namespace IodemBot.Modules.GoldenSunMechanics
{
    internal class NoEffect : IEffect
    {
        public override string Type { get; } = "NoEffect";

        public override List<string> Apply(ColossoFighter User, ColossoFighter Target)
        {
            return new List<string>();
        }

        public override string ToString()
        {
            return "Doesn't have a effect, should probably have one. Please report.";
        }

        protected override bool InternalValidSelection(ColossoFighter user)
        {
            return false;
        }
    }
}