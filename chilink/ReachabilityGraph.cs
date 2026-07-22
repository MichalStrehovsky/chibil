using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Coff;

namespace Chilink;

internal sealed class ReachabilityGraph
{
    private readonly SymbolResolver _symbols;

    public ReachabilityGraph(SymbolResolver symbols)
    {
        _symbols = symbols;
    }

    public HashSet<CoffInputSection> Compute(CoffInputSymbol entryPoint, bool optimizeReferences)
    {
        if (!optimizeReferences)
        {
            return _symbols.SelectedSections
                .Where(section => !section.IsDebug)
                .ToHashSet();
        }

        CoffInputSection entrySection = entryPoint.Input.Sections.Single(
            section => section.Handle == entryPoint.Section);
        var live = new HashSet<CoffInputSection>();
        var pending = new Queue<CoffInputSection>();
        Mark(entrySection, live, pending);
        foreach (CoffInputSection section in _symbols.SelectedSections)
        {
            if (!section.IsComdat && !section.IsDebug)
            {
                Mark(section, live, pending);
            }
        }

        while (pending.TryDequeue(out CoffInputSection section))
        {
            foreach (CoffInputRelocation relocation in section.Relocations)
            {
                CoffInputSymbol target = _symbols.ResolveRelocationTarget(section.Input, relocation);
                if (target == null || !target.IsDefined)
                {
                    continue;
                }

                CoffInputSection targetSection = target.Input.Sections.Single(
                    candidate => candidate.Handle == target.Section);
                Mark(targetSection, live, pending);
            }
        }

        return live;
    }

    private void Mark(
        CoffInputSection section,
        HashSet<CoffInputSection> live,
        Queue<CoffInputSection> pending)
    {
        CoffInputSection canonical = _symbols.GetCanonicalSection(section);
        if (_symbols.SelectedSections.Contains(canonical) && live.Add(canonical))
        {
            pending.Enqueue(canonical);
        }
    }
}
