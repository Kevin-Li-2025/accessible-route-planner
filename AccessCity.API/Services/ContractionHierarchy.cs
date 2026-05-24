using AccessCity.API.Models;

namespace AccessCity.API.Services;

/// <summary>
/// Contraction Hierarchies (CH) — a speedup technique for shortest-path queries.
///
/// Preprocessing: nodes are contracted in order of importance. When a node is
/// contracted, shortcut edges are added between its neighbours if the shortest
/// path between them passes through the contracted node. This produces a
/// hierarchy where higher-rank nodes are "more important" to the road network.
///
/// Query: bidirectional Dijkstra that explores only "upward" edges (toward
/// higher-rank nodes) from both source and target. The search spaces are tiny
/// compared to standard Dijkstra, giving O(√n · log(√n)) query time.
///
/// AccessCity builds one CH per mobility profile (standard, wheelchair, stroller)
/// because each profile has different edge traversal costs.
/// </summary>
public static class ContractionHierarchy
{
    /// <summary>
    /// Build a contraction hierarchy for the given graph using the specified
    /// edge weight function. The weight function should return traversal cost
    /// in seconds for each edge under a specific mobility profile.
    /// </summary>
    public static ContractionHierarchyData Build(
        IReadOnlyDictionary<long, GraphNode> nodes,
        Func<GraphEdge, double> weightFunction)
    {
        // Step 1: Map node IDs to compact indices
        var orderedNodes = nodes.Values.OrderBy(n => n.Id).ToArray();
        var nodeIdToIndex = new Dictionary<long, int>(orderedNodes.Length);
        var indexToNodeId = new long[orderedNodes.Length];
        for (var i = 0; i < orderedNodes.Length; i++)
        {
            nodeIdToIndex[orderedNodes[i].Id] = i;
            indexToNodeId[i] = orderedNodes[i].Id;
        }

        var n = orderedNodes.Length;

        // Step 2: Build initial adjacency (forward and backward)
        var adjForward = new List<CHEdge>[n];
        var adjBackward = new List<CHEdge>[n];
        for (var i = 0; i < n; i++)
        {
            adjForward[i] = new List<CHEdge>();
            adjBackward[i] = new List<CHEdge>();
        }

        foreach (var node in orderedNodes)
        {
            if (!nodeIdToIndex.TryGetValue(node.Id, out var fromIdx)) continue;

            foreach (var edge in node.Edges.Values)
            {
                if (!nodeIdToIndex.TryGetValue(edge.TargetNodeId, out var toIdx)) continue;

                var cost = weightFunction(edge);
                if (cost <= 0) cost = 0.001;

                adjForward[fromIdx].Add(new CHEdge(toIdx, cost, false, -1));
                adjBackward[toIdx].Add(new CHEdge(fromIdx, cost, false, -1));
            }
        }

        // Step 3: Compute node importance and contraction order
        var nodeRank = new int[n];
        var contracted = new bool[n];
        var importance = new double[n];
        var shortcutMiddleNodes = new Dictionary<(int From, int To), int>();

        for (var i = 0; i < n; i++)
        {
            importance[i] = ComputeImportance(i, adjForward, adjBackward, contracted);
        }

        var pq = new PriorityQueue<int, double>();
        for (var i = 0; i < n; i++)
        {
            pq.Enqueue(i, importance[i]);
        }

        // Step 4: Contract nodes in order of importance
        var rank = 0;
        while (pq.Count > 0)
        {
            var nodeIdx = pq.Dequeue();
            if (contracted[nodeIdx]) continue;

            // Lazy update: recompute importance and re-enqueue if not minimal
            var updatedImportance = ComputeImportance(nodeIdx, adjForward, adjBackward, contracted);
            if (pq.Count > 0 && pq.TryPeek(out _, out var minPriority) && updatedImportance > minPriority + 1e-9)
            {
                pq.Enqueue(nodeIdx, updatedImportance);
                continue;
            }

            // Contract this node
            contracted[nodeIdx] = true;
            nodeRank[nodeIdx] = rank++;

            // Find shortcuts needed
            var shortcuts = FindShortcuts(nodeIdx, adjForward, adjBackward, contracted);
            foreach (var shortcut in shortcuts)
            {
                adjForward[shortcut.From].Add(new CHEdge(shortcut.To, shortcut.Cost, true, nodeIdx));
                adjBackward[shortcut.To].Add(new CHEdge(shortcut.From, shortcut.Cost, true, nodeIdx));
                shortcutMiddleNodes[(shortcut.From, shortcut.To)] = nodeIdx;
            }
        }

        // Step 5: Build adjacency lists for directed CH query.
        // Forward search follows original edges to higher-ranked nodes.
        // Backward search runs on the reversed graph, which corresponds to
        // incoming original edges from higher-ranked nodes.
        var upwardEdges = new Dictionary<int, List<CHEdge>>(n);
        var reverseUpwardEdges = new Dictionary<int, List<CHEdge>>(n);
        for (var i = 0; i < n; i++)
        {
            foreach (var edge in adjForward[i])
            {
                if (nodeRank[edge.TargetIndex] > nodeRank[i])
                {
                    AddQueryEdge(upwardEdges, i, edge);
                }
                else if (nodeRank[i] > nodeRank[edge.TargetIndex])
                {
                    AddQueryEdge(
                        reverseUpwardEdges,
                        edge.TargetIndex,
                        new CHEdge(i, edge.CostSeconds, edge.IsShortcut, edge.MiddleNodeIndex));
                }
            }
        }

        return new ContractionHierarchyData
        {
            NodeCount = n,
            NodeRank = nodeRank,
            NodeIdToIndex = nodeIdToIndex,
            IndexToNodeId = indexToNodeId,
            UpwardEdges = upwardEdges,
            ReverseUpwardEdges = reverseUpwardEdges,
            ShortcutMiddleNodes = shortcutMiddleNodes
        };
    }

    /// <summary>
    /// Query the shortest path cost between two nodes using bidirectional
    /// upward-only Dijkstra on the contraction hierarchy.
    /// Returns the shortest path cost in seconds, or double.PositiveInfinity if unreachable.
    /// </summary>
    public static CHQueryResult Query(
        ContractionHierarchyData ch,
        long sourceNodeId,
        long targetNodeId)
    {
        if (!ch.NodeIdToIndex.TryGetValue(sourceNodeId, out var sourceIdx)
            || !ch.NodeIdToIndex.TryGetValue(targetNodeId, out var targetIdx))
        {
            return new CHQueryResult(double.PositiveInfinity, null);
        }

        if (sourceIdx == targetIdx)
        {
            return new CHQueryResult(0, new[] { sourceNodeId });
        }

        var n = ch.NodeCount;
        var distForward = new double[n];
        var distBackward = new double[n];
        var parentForward = new CHParentEdge?[n];
        var parentBackward = new CHParentEdge?[n];
        Array.Fill(distForward, double.PositiveInfinity);
        Array.Fill(distBackward, double.PositiveInfinity);

        distForward[sourceIdx] = 0;
        distBackward[targetIdx] = 0;

        var fwdQueue = new PriorityQueue<int, double>();
        var bwdQueue = new PriorityQueue<int, double>();
        fwdQueue.Enqueue(sourceIdx, 0);
        bwdQueue.Enqueue(targetIdx, 0);

        var settledForward = new HashSet<int>();
        var settledBackward = new HashSet<int>();

        var bestCost = double.PositiveInfinity;
        var meetNode = -1;

        // Forward search: explore upward edges from source
        while (fwdQueue.Count > 0 || bwdQueue.Count > 0)
        {
            // Forward step
            if (fwdQueue.Count > 0)
            {
                fwdQueue.TryPeek(out _, out var fwdMin);
                if (fwdMin < bestCost)
                {
                    var u = fwdQueue.Dequeue();
                    if (!settledForward.Add(u)) goto BackwardStep;

                    // Check if backward search has settled this node
                    if (distForward[u] + distBackward[u] < bestCost)
                    {
                        bestCost = distForward[u] + distBackward[u];
                        meetNode = u;
                    }

                    if (ch.UpwardEdges.TryGetValue(u, out var edges))
                    {
                        foreach (var edge in edges)
                        {
                            var newDist = distForward[u] + edge.CostSeconds;
                            if (newDist < distForward[edge.TargetIndex])
                            {
                                distForward[edge.TargetIndex] = newDist;
                                parentForward[edge.TargetIndex] = new CHParentEdge(
                                    u,
                                    edge.TargetIndex,
                                    edge.MiddleNodeIndex);
                                fwdQueue.Enqueue(edge.TargetIndex, newDist);
                            }
                        }
                    }
                }
            }

        BackwardStep:
            // Backward step: explore upward edges from target (reversed)
            if (bwdQueue.Count > 0)
            {
                bwdQueue.TryPeek(out _, out var bwdMin);
                if (bwdMin < bestCost)
                {
                    var v = bwdQueue.Dequeue();
                    if (!settledBackward.Add(v)) continue;

                    if (distForward[v] + distBackward[v] < bestCost)
                    {
                        bestCost = distForward[v] + distBackward[v];
                        meetNode = v;
                    }

                    if (ch.ReverseUpwardEdges.TryGetValue(v, out var edges))
                    {
                        foreach (var edge in edges)
                        {
                            var newDist = distBackward[v] + edge.CostSeconds;
                            if (newDist < distBackward[edge.TargetIndex])
                            {
                                distBackward[edge.TargetIndex] = newDist;
                                parentBackward[edge.TargetIndex] = new CHParentEdge(
                                    edge.TargetIndex,
                                    v,
                                    edge.MiddleNodeIndex);
                                bwdQueue.Enqueue(edge.TargetIndex, newDist);
                            }
                        }
                    }
                }
            }

            // Termination: both queues have minimum > bestCost
            double fMin = double.PositiveInfinity, bMin = double.PositiveInfinity;
            if (fwdQueue.Count > 0) fwdQueue.TryPeek(out _, out fMin);
            if (bwdQueue.Count > 0) bwdQueue.TryPeek(out _, out bMin);

            if (Math.Min(fMin, bMin) >= bestCost) break;
        }

        if (double.IsPositiveInfinity(bestCost) || meetNode < 0)
        {
            return new CHQueryResult(double.PositiveInfinity, null);
        }

        // Reconstruct path
        var path = ReconstructPath(
            parentForward,
            parentBackward,
            meetNode,
            sourceIdx,
            targetIdx,
            ch.IndexToNodeId,
            ch.ShortcutMiddleNodes);
        return path is null
            ? new CHQueryResult(double.PositiveInfinity, null)
            : new CHQueryResult(bestCost, path);
    }

    /// <summary>
    /// Build per-profile CH data for standard, wheelchair, and stroller profiles.
    /// </summary>
    public static ContractionHierarchySet BuildForAllProfiles(
        IReadOnlyDictionary<long, GraphNode> nodes)
    {
        var standard = Build(nodes, edge =>
            RouteEdgeCostModel.ComputeTraversalSeconds(
                edge.DistanceMetres,
                edge.StandardAccessibilityPenaltySeconds,
                "standard"));

        var wheelchair = Build(nodes, edge =>
            RouteEdgeCostModel.ComputeTraversalSeconds(
                edge.DistanceMetres,
                edge.WheelchairAccessibilityPenaltySeconds,
                "manual-wheelchair"));

        var stroller = Build(nodes, edge =>
            RouteEdgeCostModel.ComputeTraversalSeconds(
                edge.DistanceMetres,
                edge.StrollerAccessibilityPenaltySeconds,
                "stroller"));

        return new ContractionHierarchySet
        {
            Standard = standard,
            Wheelchair = wheelchair,
            Stroller = stroller
        };
    }

    /// <summary>
    /// Resolve the appropriate CH for a given mobility profile.
    /// </summary>
    public static ContractionHierarchyData? ResolveForProfile(
        ContractionHierarchySet? chSet,
        string? profile)
    {
        if (chSet is null) return null;
        return profile?.ToLowerInvariant() switch
        {
            "manual-wheelchair" or "power-wheelchair" => chSet.Wheelchair,
            "stroller" => chSet.Stroller,
            _ => chSet.Standard
        };
    }

    // ──────── Node importance heuristic ────────

    private static double ComputeImportance(
        int nodeIdx,
        IReadOnlyList<List<CHEdge>> adjForward,
        IReadOnlyList<List<CHEdge>> adjBackward,
        IReadOnlyList<bool> contracted)
    {
        // Edge difference: shortcuts_added - edges_removed
        var inEdges = adjBackward[nodeIdx].Count(e => !contracted[e.TargetIndex]);
        var outEdges = adjForward[nodeIdx].Count(e => !contracted[e.TargetIndex]);
        var edgesRemoved = inEdges + outEdges;

        // Count shortcuts that would be needed
        var shortcutsNeeded = CountShortcuts(nodeIdx, adjForward, adjBackward, contracted);
        var edgeDifference = shortcutsNeeded - edgesRemoved;

        // Contracted neighbours: prefer contracting nodes whose neighbours are not yet contracted
        var contractedNeighbours = 0;
        foreach (var edge in adjForward[nodeIdx])
        {
            if (contracted[edge.TargetIndex]) contractedNeighbours++;
        }
        foreach (var edge in adjBackward[nodeIdx])
        {
            if (contracted[edge.TargetIndex]) contractedNeighbours++;
        }

        return edgeDifference * 10.0 + contractedNeighbours * 1.0;
    }

    private static int CountShortcuts(
        int nodeIdx,
        IReadOnlyList<List<CHEdge>> adjForward,
        IReadOnlyList<List<CHEdge>> adjBackward,
        IReadOnlyList<bool> contracted)
    {
        var count = 0;
        foreach (var inEdge in adjBackward[nodeIdx])
        {
            if (contracted[inEdge.TargetIndex]) continue;
            var u = inEdge.TargetIndex;
            var costUV = inEdge.CostSeconds;

            foreach (var outEdge in adjForward[nodeIdx])
            {
                if (contracted[outEdge.TargetIndex]) continue;
                var w = outEdge.TargetIndex;
                if (u == w) continue;

                var shortcutCost = costUV + outEdge.CostSeconds;
                if (!HasWitnessPath(u, w, shortcutCost, nodeIdx, adjForward, contracted))
                {
                    count++;
                }
            }
        }
        return count;
    }

    private static List<(int From, int To, double Cost)> FindShortcuts(
        int nodeIdx,
        IReadOnlyList<List<CHEdge>> adjForward,
        IReadOnlyList<List<CHEdge>> adjBackward,
        IReadOnlyList<bool> contracted)
    {
        var shortcuts = new List<(int From, int To, double Cost)>();
        foreach (var inEdge in adjBackward[nodeIdx])
        {
            if (contracted[inEdge.TargetIndex]) continue;
            var u = inEdge.TargetIndex;
            var costUV = inEdge.CostSeconds;

            foreach (var outEdge in adjForward[nodeIdx])
            {
                if (contracted[outEdge.TargetIndex]) continue;
                var w = outEdge.TargetIndex;
                if (u == w) continue;

                var shortcutCost = costUV + outEdge.CostSeconds;
                if (!HasWitnessPath(u, w, shortcutCost, nodeIdx, adjForward, contracted))
                {
                    shortcuts.Add((u, w, shortcutCost));
                }
            }
        }
        return shortcuts;
    }

    /// <summary>
    /// Check if there's a witness path from u to w (not through nodeIdx)
    /// that is shorter than maxCost.
    /// Uses a bounded Dijkstra with a hop limit for efficiency.
    /// </summary>
    private static bool HasWitnessPath(
        int from, int to, double maxCost, int excludeNode,
        IReadOnlyList<List<CHEdge>> adjForward,
        IReadOnlyList<bool> contracted)
    {
        const int MaxHops = 5;

        var dist = new Dictionary<int, double> { [from] = 0 };
        var queue = new PriorityQueue<(int Node, int Hops), double>();
        queue.Enqueue((from, 0), 0);

        while (queue.Count > 0)
        {
            var (current, hops) = queue.Dequeue();
            var currentDist = dist.GetValueOrDefault(current, double.PositiveInfinity);

            if (current == to) return true;
            if (hops >= MaxHops) continue;
            if (currentDist >= maxCost) continue;

            foreach (var edge in adjForward[current])
            {
                if (edge.TargetIndex == excludeNode) continue;
                if (contracted[edge.TargetIndex]) continue;

                var newDist = currentDist + edge.CostSeconds;
                if (newDist >= maxCost) continue;

                if (newDist < dist.GetValueOrDefault(edge.TargetIndex, double.PositiveInfinity))
                {
                    dist[edge.TargetIndex] = newDist;
                    queue.Enqueue((edge.TargetIndex, hops + 1), newDist);
                }
            }
        }

        return false;
    }

    private static long[]? ReconstructPath(
        CHParentEdge?[] parentForward,
        CHParentEdge?[] parentBackward,
        int meetNode,
        int sourceIdx,
        int targetIdx,
        long[] indexToNodeId,
        IReadOnlyDictionary<(int From, int To), int> shortcutMiddleNodes)
    {
        var edges = new List<CHParentEdge>();

        // Forward path edges: source -> meetNode.
        var current = meetNode;
        while (current != -1 && current != sourceIdx)
        {
            var parent = parentForward[current];
            if (parent is null)
            {
                return null;
            }

            edges.Add(parent.Value);
            current = parent.Value.FromIndex;
        }

        if (current != sourceIdx)
        {
            return null;
        }

        edges.Reverse();

        // Backward search parents already point in original path direction:
        // current -> next node toward target.
        current = meetNode;
        while (current != -1 && current != targetIdx)
        {
            var parent = parentBackward[current];
            if (parent is null)
            {
                return null;
            }

            edges.Add(parent.Value);
            current = parent.Value.ToIndex;
        }

        if (current != targetIdx)
        {
            return null;
        }

        if (edges.Count == 0)
        {
            return new[] { indexToNodeId[sourceIdx] };
        }

        var expandedPath = new List<int>();
        foreach (var edge in edges)
        {
            AppendExpandedEdge(
                edge.FromIndex,
                edge.ToIndex,
                edge.MiddleNodeIndex,
                shortcutMiddleNodes,
                expandedPath,
                new HashSet<(int From, int To)>());
        }

        return expandedPath.Select(idx => indexToNodeId[idx]).ToArray();
    }

    private static void AddQueryEdge(
        IDictionary<int, List<CHEdge>> adjacency,
        int from,
        CHEdge edge)
    {
        if (!adjacency.TryGetValue(from, out var edges))
        {
            edges = new List<CHEdge>();
            adjacency[from] = edges;
        }

        edges.Add(edge);
    }

    private static void AppendExpandedEdge(
        int from,
        int to,
        int middle,
        IReadOnlyDictionary<(int From, int To), int> shortcutMiddleNodes,
        List<int> path,
        HashSet<(int From, int To)> stack)
    {
        if (path.Count == 0)
        {
            path.Add(from);
        }
        else if (path[^1] != from)
        {
            path.Add(from);
        }

        if (middle < 0)
        {
            shortcutMiddleNodes.TryGetValue((from, to), out middle);
            if (middle == 0 && !shortcutMiddleNodes.ContainsKey((from, to)))
            {
                middle = -1;
            }
        }

        if (middle >= 0 && middle != from && middle != to && stack.Add((from, to)))
        {
            AppendExpandedEdge(from, middle, -1, shortcutMiddleNodes, path, stack);
            AppendExpandedEdge(middle, to, -1, shortcutMiddleNodes, path, stack);
            stack.Remove((from, to));
            return;
        }

        path.Add(to);
    }
}

/// <summary>
/// Result of a CH shortest-path query.
/// </summary>
public sealed record CHQueryResult(
    double CostSeconds,
    long[]? PathNodeIds);

/// <summary>
/// Edge in the contraction hierarchy graph.
/// </summary>
public readonly record struct CHEdge(
    int TargetIndex,
    double CostSeconds,
    bool IsShortcut,
    int MiddleNodeIndex);

public readonly record struct CHParentEdge(
    int FromIndex,
    int ToIndex,
    int MiddleNodeIndex);

/// <summary>
/// Contraction hierarchy data for a single mobility profile.
/// </summary>
public sealed class ContractionHierarchyData
{
    public int NodeCount { get; init; }
    public int[] NodeRank { get; init; } = Array.Empty<int>();
    public Dictionary<long, int> NodeIdToIndex { get; init; } = new();
    public long[] IndexToNodeId { get; init; } = Array.Empty<long>();
    public Dictionary<int, List<CHEdge>> UpwardEdges { get; init; } = new();
    public Dictionary<int, List<CHEdge>> ReverseUpwardEdges { get; init; } = new();
    public Dictionary<(int From, int To), int> ShortcutMiddleNodes { get; init; } = new();
}

/// <summary>
/// Set of contraction hierarchies for all mobility profiles.
/// </summary>
public sealed class ContractionHierarchySet
{
    public ContractionHierarchyData? Standard { get; init; }
    public ContractionHierarchyData? Wheelchair { get; init; }
    public ContractionHierarchyData? Stroller { get; init; }
}
