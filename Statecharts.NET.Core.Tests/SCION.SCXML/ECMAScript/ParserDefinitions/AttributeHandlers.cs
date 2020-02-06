﻿using Statecharts.NET.Tests.Definition;
using Statecharts.NET.Tests.SCION.SCXML.Definition;
using Statecharts.NET.Utilities;

namespace Statecharts.NET.Tests.SCION.SCXML.ECMAScript.ParserDefinitions
{
    internal static class Attribute
    {
        internal static void SetStatechartInitial(Statechart statechart, string initialStateNode)
            => statechart.InitialStateNodeName = initialStateNode.ToOption();
        internal static void SetStateNodeName(AtomicStateNode stateNode, string name) =>
            stateNode._name = name;
    }
}
