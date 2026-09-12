using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Modules.Hatch;

/// <summary>
/// One definition of "below this issue", and one of "what it adds up to".
///
/// Every meter Hatch draws - the Plan page, the issue page, anything holding an
/// API key - is drawn from this file, computed on the server for the reason the
/// rank and <see cref="WorkController"/>'s pick are computed on the server: it
/// keeps every client dumb. A browser that re-derived a subtree's total would
/// disagree with the CLI the first time a column was added.
///
/// The unit is the <em>leaf</em> - an issue with no children. A subtree's
/// rollup is the histogram of its leaf descendants by status, and an issue with
/// no children rolls up as itself, one leaf in its own column. That is what
/// makes a parent's rollup exactly the sum of its children's, which is what
/// lets a stack of meters agree with the one above it.
///
/// A parent's own column never lands in its own meter: a story sitting in
/// review whose tasks are all in todo reads as todo, because the tasks are the
/// work. Ready dates are not consulted either - a card folded off the board is
/// still work, and a total that shrank and grew as dates arrived would not be a
/// total.
/// </summary>
public static class Rollup
{
    /// <summary>
    /// The whole tracker, folded once. Load it, then ask it about as many
    /// issues as you like - the Plan page asks about every epic in the house.
    /// </summary>
    public static async Task<Tree> LoadAsync(HatchContext db, CancellationToken ct)
    {
        var statuses = await db.Statuses.AsNoTracking()
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.IsTerminal, s.IsDeferred })
            .ToListAsync(ct);

        var nodes = await NodesAsync(db, ct);

        // The one definition of "open" lives in Questions.cs, and this is a
        // caller rather than a second copy - "waiting on a person" has to mean
        // the same thing on a meter as it does on a card and in a refusal.
        var waiting = await Questions.OpenCountsAsync(db, ct);

        return new Tree(
            statuses.Select(s => (s.Id, s.IsTerminal, s.IsDeferred)).ToList(),
            nodes,
            waiting);
    }

    /// <summary>
    /// Every issue below this one, at any depth, and not the issue itself -
    /// "under AER-1" is a question about what hangs beneath it.
    /// </summary>
    /// <remarks>
    /// Shared with the <c>ancestorKey</c> filter on
    /// <see cref="IssuesController.SearchIssues"/> rather than written twice:
    /// two definitions of "descendant" is the divergence nobody notices until a
    /// filter and a meter disagree about the same epic.
    /// </remarks>
    public static async Task<List<long>> DescendantIdsAsync(HatchContext db, long rootId, CancellationToken ct) =>
        Descend(ChildIndex(await NodesAsync(db, ct)), rootId);

    // ---- Loading ----

    /// <summary>
    /// <c>(Id, ParentId, StatusId)</c> for the whole tracker, in one query.
    /// Three columns and no joins: this is the shape of the tree and nothing
    /// else, and it is read whole because folding it is O(n) once while walking
    /// it per node is O(n) per node.
    /// </summary>
    /// <remarks>
    /// Rank order, so the child lists it builds come out in the order the board
    /// draws them and nothing downstream has to sort again.
    /// </remarks>
    private static async Task<List<Node>> NodesAsync(HatchContext db, CancellationToken ct)
    {
        var rows = await db.Issues.AsNoTracking()
            .OrderBy(i => i.Rank)
            .ThenBy(i => i.Id)
            .Select(i => new { i.Id, i.ParentId, i.StatusId })
            .ToListAsync(ct);

        return rows.Select(r => new Node(r.Id, r.ParentId, r.StatusId)).ToList();
    }

    /// <summary>One issue, as the fold reads it. Nothing else about it matters here.</summary>
    internal readonly record struct Node(long Id, long? ParentId, int StatusId);

    /// <summary>Shared, because a childless issue is the common case and each one would otherwise allocate a list to say so.</summary>
    private static readonly List<long> NoChildren = [];

    private static Dictionary<long, List<long>> ChildIndex(List<Node> nodes)
    {
        var children = new Dictionary<long, List<long>>();
        var known = nodes.Select(n => n.Id).ToHashSet();

        foreach (var node in nodes)
        {
            // A parent id pointing at nothing is an outdent that half-happened;
            // treat the child as a root rather than losing it out of every total.
            if (node.ParentId is not { } parent || !known.Contains(parent)) continue;

            if (!children.TryGetValue(parent, out var siblings)) children[parent] = siblings = [];
            siblings.Add(node.Id);
        }

        return children;
    }

    // ---- Walking ----

    /// <summary>
    /// Breadth-first, a generation at a time, with a <c>seen</c> set that is
    /// not decoration: parenting refuses to close a loop, but a loop that got
    /// in some other way - a restored backup, a hand-written UPDATE - must make
    /// this return a wrong answer rather than spin forever.
    /// </summary>
    private static List<long> Descend(Dictionary<long, List<long>> children, long rootId)
    {
        var seen = new HashSet<long> { rootId };
        var found = new List<long>();
        var generation = new List<long> { rootId };

        while (generation.Count > 0)
        {
            var next = new List<long>();

            foreach (var id in generation)
            foreach (var child in children.TryGetValue(id, out var kids) ? kids : NoChildren)
                if (seen.Add(child))
                    next.Add(child);

            found.AddRange(next);
            generation = next;
        }

        return found;
    }

    // ---- The tree ----

    /// <summary>
    /// The tracker's shape plus its arithmetic, folded on demand and remembered
    /// - so asking about an epic and then about each of its stories costs one
    /// fold of the subtree, not one per question.
    /// </summary>
    public sealed class Tree
    {
        private readonly IReadOnlyList<(int Id, bool IsTerminal, bool IsDeferred)> statuses;
        private readonly Dictionary<long, int> statusOf;
        private readonly Dictionary<long, List<long>> children;
        private readonly Dictionary<long, int> waiting;
        private readonly Dictionary<long, Subtree> folded = [];

        /// <summary>A tree is loaded through <see cref="Rollup.LoadAsync"/>, which is the only thing that knows how to fill one.</summary>
        internal Tree(
            IReadOnlyList<(int Id, bool IsTerminal, bool IsDeferred)> statuses,
            List<Node> nodes,
            Dictionary<long, int> waiting)
        {
            this.statuses = statuses;
            this.waiting = waiting;
            statusOf = nodes.ToDictionary(n => n.Id, n => n.StatusId);
            children = ChildIndex(nodes);
        }

        /// <summary>
        /// The direct children, in rank order. What a client draws a row per.
        /// </summary>
        public IReadOnlyList<long> ChildrenOf(long id) => children.TryGetValue(id, out var kids) ? kids : NoChildren;

        /// <summary>
        /// No children. Carried on the wire rather than inferred from
        /// <see cref="RollupDto.Leaves"/> being one, because a story with a
        /// single task and a task with none must not look alike to a client
        /// deciding between a status pill and a bar.
        /// </summary>
        public bool IsLeaf(long id) => ChildrenOf(id).Count == 0;

        /// <summary>What this subtree adds up to.</summary>
        public RollupDto Of(long id) => Shape(Fold(id));

        /// <summary>
        /// What several subtrees add up to together - the Plan page's
        /// <c>loose</c>, which is the work hanging under no epic at all and so
        /// is a total over many roots rather than one.
        /// </summary>
        /// <remarks>
        /// The caller owes the disjointness: these are summed, not unioned, so
        /// an id passed alongside one of its own ancestors is counted twice.
        /// Roots of the tracker cannot contain one another, which is why the
        /// one caller is safe.
        /// </remarks>
        public RollupDto Of(IEnumerable<long> ids)
        {
            var leaves = new Dictionary<int, int>();
            var waiting = 0;

            foreach (var id in ids)
            {
                var subtree = Fold(id);

                foreach (var (status, count) in subtree.Leaves)
                    leaves[status] = leaves.GetValueOrDefault(status) + count;

                waiting += subtree.Waiting;
            }

            return Shape(new Subtree(leaves, waiting));
        }

        /// <summary>A folded total, dressed for the wire.</summary>
        /// <remarks>
        /// Deferred leaves are left out of all three numbers rather than
        /// counted as done or as outstanding. "12 of 20" is a promise about
        /// work somebody still intends to do, and a shelved ticket is neither
        /// half of it: counting it done would have the meter claim something
        /// shipped that never did, and counting it outstanding would leave an
        /// epic that is finished except for three parked tasks stuck at 85%
        /// forever, which is how a progress bar stops being read.
        ///
        /// <para>The slices go with the total for the arithmetic's sake as much
        /// as the meaning's: the bar's segments are shares of <c>leaves</c>, and
        /// a stripe drawn from a count that is not in the denominator is a bar
        /// that does not add up to itself.</para>
        /// </remarks>
        private RollupDto Shape(Subtree subtree)
        {
            var counted = statuses.Where(s => !s.IsDeferred).ToList();

            // Board order, and a status no leaf is sitting in is absent rather
            // than zero: the client already holds the column list and does not
            // need a row that draws nothing.
            var slices = counted
                .Where(s => subtree.Leaves.ContainsKey(s.Id))
                .Select(s => new RollupSliceDto(s.Id, subtree.Leaves[s.Id]))
                .ToList();

            var done = counted.Where(s => s.IsTerminal).Sum(s => subtree.Leaves.GetValueOrDefault(s.Id));
            var leaves = counted.Sum(s => subtree.Leaves.GetValueOrDefault(s.Id));

            return new RollupDto(leaves, done, subtree.Waiting, slices);
        }

        /// <summary>A subtree's two totals: its leaves by status, and the questions under it nobody has answered.</summary>
        private readonly record struct Subtree(Dictionary<int, int> Leaves, int Waiting);

        /// <summary>
        /// Post-order, iteratively, memoised across calls - so folding the
        /// whole tracker costs O(n) however many issues are asked about.
        /// </summary>
        /// <remarks>
        /// An explicit stack rather than recursion, and a set of the nodes on
        /// the path down rather than a depth limit: a cycle in the parent
        /// column stops the descent where it closes, leaving that branch out of
        /// the total. Wrong, and finished - which is the only pair of
        /// properties available once the data is impossible.
        /// </remarks>
        private Subtree Fold(long root)
        {
            if (folded.TryGetValue(root, out var already)) return already;
            if (!statusOf.ContainsKey(root)) return new Subtree([], 0);

            var onPath = new HashSet<long>();
            var stack = new Stack<(long Id, bool Expanded)>();
            stack.Push((root, false));

            while (stack.Count > 0)
            {
                var (id, expanded) = stack.Pop();

                if (!expanded)
                {
                    if (folded.ContainsKey(id) || !onPath.Add(id)) continue;

                    stack.Push((id, true));
                    foreach (var child in ChildrenOf(id)) stack.Push((child, false));
                    continue;
                }

                onPath.Remove(id);

                var leaves = new Dictionary<int, int>();
                var mine = ChildrenOf(id);

                if (mine.Count == 0)
                {
                    // A leaf is one unit of work, in its own column. Everything
                    // above it is a sum of these and nothing else.
                    leaves[statusOf.GetValueOrDefault(id)] = 1;
                }
                else
                {
                    foreach (var child in mine)
                    {
                        if (!folded.TryGetValue(child, out var sub)) continue;

                        foreach (var (status, count) in sub.Leaves)
                            leaves[status] = leaves.GetValueOrDefault(status) + count;
                    }
                }

                // Waiting is the one total that counts the issue itself: an
                // epic blocked on a person is blocked whether the question was
                // asked on the epic or two levels under it.
                var stuck = waiting.GetValueOrDefault(id)
                    + mine.Sum(child => folded.TryGetValue(child, out var sub) ? sub.Waiting : 0);

                folded[id] = new Subtree(leaves, stuck);
            }

            return folded.GetValueOrDefault(root, new Subtree([], 0));
        }
    }
}
