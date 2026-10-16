// 仅处理调用方单次解析的真实航道事实，不依赖Unity，不保存GameState或跨调用缓存。
using System;
using System.Collections.Generic;

namespace YC.Domain.Travel
{
    public sealed class RouteGraphRoute
    {
        public string RouteId { get; }
        public IReadOnlyList<string> CoveredLocationIds { get; }
        public bool IsFree { get; }
        public int TollGoldVoucherAmount { get; }

        public RouteGraphRoute(string routeId, IReadOnlyList<string> locations, bool isFree, int toll)
        {
            RouteId = routeId;
            CoveredLocationIds = locations == null ? null : Array.AsReadOnly(Copy(locations));
            IsFree = isFree;
            TollGoldVoucherAmount = toll;
        }

        private static string[] Copy(IReadOnlyList<string> values)
        {
            var copy = new string[values.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = values[i];
            return copy;
        }
    }

    public sealed class RouteGraphResult
    {
        public bool IsValid { get; internal set; }
        public string Diagnostic { get; internal set; } = string.Empty;
        public bool HasFinalPlan { get; internal set; }
        public IReadOnlyList<string> ReachableLocationIds { get; internal set; } = Array.AsReadOnly(new string[0]);
        public IReadOnlyList<string> SelectableReachableLocationIds { get; internal set; } = Array.AsReadOnly(new string[0]);
        public IReadOnlyList<string> AutomaticallySelectedFreeRouteIds { get; internal set; } = Array.AsReadOnly(new string[0]);
        public IReadOnlyList<string> FrontierRouteIds { get; internal set; } = Array.AsReadOnly(new string[0]);
        public IReadOnlyList<string> OriginalConfirmedRouteIds { get; internal set; } = Array.AsReadOnly(new string[0]);
        public IReadOnlyList<string> EffectiveRouteIds { get; internal set; } = Array.AsReadOnly(new string[0]);
        public IReadOnlyList<string> ChargeRouteIds { get; internal set; } = Array.AsReadOnly(new string[0]);
        public IReadOnlyList<string> PrunedRouteIds { get; internal set; } = Array.AsReadOnly(new string[0]);
        public IReadOnlyList<string> PrunedFreeRouteIds { get; internal set; } = Array.AsReadOnly(new string[0]);
        public int ProvisionalGoldVoucherAmount { get; internal set; }
        public int FinalGoldVoucherAmount { get; internal set; }
        public int VertexCount { get; internal set; }
        public int IncidenceCount { get; internal set; }
        public long WorkUnits { get; internal set; }
    }

    public static class RouteSelectionGraphQuery
    {
        // 明确完成0点是独立输入；空编辑草稿不会被剪空。
        public static RouteGraphResult Query(
            IReadOnlyList<string> locationIds, IReadOnlyList<RouteGraphRoute> routes,
            string startLocationId, IReadOnlyList<string> confirmedRouteIds,
            IReadOnlyList<string> selectableLocationIds, IReadOnlyList<string> selectedLocationIds,
            bool confirmEmptySelection = false)
        {
            if (locationIds == null) throw new ArgumentNullException(nameof(locationIds));
            if (routes == null) throw new ArgumentNullException(nameof(routes));
            if (startLocationId == null) throw new ArgumentNullException(nameof(startLocationId));
            if (confirmedRouteIds == null) throw new ArgumentNullException(nameof(confirmedRouteIds));
            if (selectableLocationIds == null) throw new ArgumentNullException(nameof(selectableLocationIds));
            if (selectedLocationIds == null) throw new ArgumentNullException(nameof(selectedLocationIds));

            var result = new RouteGraphResult();
            var locations = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < locationIds.Count; i++)
            {
                result.WorkUnits++;
                if (string.IsNullOrEmpty(locationIds[i]) || locations.ContainsKey(locationIds[i]))
                    return Fail(result, "invalid_location_catalog");
                locations.Add(locationIds[i], i);
            }
            int start;
            if (!locations.TryGetValue(startLocationId, out start)) return Fail(result, "unknown_start_location");
            var routeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var covered = new List<int>[routes.Count];
            for (int i = 0; i < routes.Count; i++)
            {
                result.WorkUnits++;
                var route = routes[i];
                if (route == null || string.IsNullOrEmpty(route.RouteId) || routeIndex.ContainsKey(route.RouteId)
                    || route.CoveredLocationIds == null || route.TollGoldVoucherAmount < 0
                    || (route.IsFree && route.TollGoldVoucherAmount != 0))
                    return Fail(result, "invalid_route_catalog");
                routeIndex.Add(route.RouteId, i);
                covered[i] = new List<int>();
                var seen = new HashSet<int>();
                foreach (string id in route.CoveredLocationIds)
                {
                    result.WorkUnits++;
                    int index;
                    if (id == null || !locations.TryGetValue(id, out index)) return Fail(result, "unknown_route_location");
                    if (seen.Add(index)) covered[i].Add(index);
                }
                if (covered[i].Count < 2) return Fail(result, "route_requires_distinct_locations");
            }
            var confirmed = new HashSet<int>();
            foreach (string id in confirmedRouteIds)
            {
                result.WorkUnits++;
                int index;
                if (id == null || !routeIndex.TryGetValue(id, out index)) return Fail(result, "unknown_confirmed_route");
                if (!confirmed.Add(index)) return Fail(result, "duplicate_confirmed_route");
            }
            var selectable = new HashSet<int>();
            foreach (string id in selectableLocationIds)
            {
                result.WorkUnits++;
                int index;
                if (id == null || !locations.TryGetValue(id, out index)) return Fail(result, "unknown_selectable_location");
                if (!selectable.Add(index)) return Fail(result, "duplicate_selectable_location");
            }
            var selected = new HashSet<int>();
            foreach (string id in selectedLocationIds)
            {
                result.WorkUnits++;
                int index;
                if (id == null || !locations.TryGetValue(id, out index) || !selectable.Contains(index))
                    return Fail(result, "selected_location_outside_input");
                if (!selected.Add(index)) return Fail(result, "duplicate_selected_location");
            }
            if (confirmEmptySelection && selected.Count != 0) return Fail(result, "empty_confirmation_has_targets");

            int vertexCount = locationIds.Count + routes.Count;
            var graph = new Graph(vertexCount);
            for (int i = 0; i < routes.Count; i++)
            {
                result.WorkUnits++;
                if (!routes[i].IsFree && !confirmed.Contains(i)) continue;
                foreach (int location in covered[i])
                {
                    result.WorkUnits++;
                    graph.AddEdge(location, locationIds.Count + i);
                }
            }
            result.VertexCount = vertexCount;
            result.IncidenceCount = graph.Edges.Count;
            bool[] reachable = graph.Reachable(start, result);
            var reachableIds = new List<string>();
            for (int i = 0; i < locationIds.Count; i++)
            {
                result.WorkUnits++;
                if (reachable[i]) reachableIds.Add(locationIds[i]);
            }
            result.ReachableLocationIds = reachableIds.AsReadOnly();
            var selectableReachable = new List<string>();
            foreach (string id in selectableLocationIds)
            {
                result.WorkUnits++;
                if (reachable[locations[id]]) selectableReachable.Add(id);
            }
            result.SelectableReachableLocationIds = selectableReachable.AsReadOnly();
            var freeIds = new List<string>();
            var frontierIds = new List<string>();
            for (int i = 0; i < routes.Count; i++)
            {
                result.WorkUnits++;
                if (routes[i].IsFree)
                {
                    if (reachable[locationIds.Count + i]) freeIds.Add(routes[i].RouteId);
                }
                else if (!confirmed.Contains(i))
                {
                    bool touches = false;
                    foreach (int location in covered[i])
                    {
                        result.WorkUnits++;
                        if (reachable[location]) touches = true;
                    }
                    if (touches) frontierIds.Add(routes[i].RouteId);
                }
            }
            result.AutomaticallySelectedFreeRouteIds = freeIds.AsReadOnly();
            result.FrontierRouteIds = frontierIds.AsReadOnly();
            result.OriginalConfirmedRouteIds = Copy(confirmedRouteIds);
            foreach (int index in confirmed)
            {
                result.WorkUnits++;
                if (!reachable[locationIds.Count + index]) return Fail(result, "confirmed_route_disconnected");
                result.ProvisionalGoldVoucherAmount = checked(result.ProvisionalGoldVoucherAmount + routes[index].TollGoldVoucherAmount);
            }
            foreach (int index in selected)
            {
                result.WorkUnits++;
                if (!reachable[index]) return Fail(result, "selected_location_unreachable");
            }
            result.IsValid = true;
            if (selected.Count == 0 && !confirmEmptySelection) return result;
            result.HasFinalPlan = true;
            var keptRoutes = new bool[routes.Count];
            bool hasOtherTarget = false;
            foreach (int index in selected) if (index != start) hasOtherTarget = true;
            if (hasOtherTarget)
            {
                bool[] keptEdges = graph.KeepTerminalBlocks(start, selected, result);
                for (int i = 0; i < keptEdges.Length; i++)
                {
                    result.WorkUnits++;
                    if (!keptEdges[i]) continue;
                    var edge = graph.Edges[i];
                    int routeVertex = edge.A >= locationIds.Count ? edge.A : edge.B;
                    keptRoutes[routeVertex - locationIds.Count] = true;
                }
            }
            var effective = new List<string>();
            var charge = new List<string>();
            var prunedFree = new List<string>();
            for (int i = 0; i < routes.Count; i++)
            {
                result.WorkUnits++;
                if (keptRoutes[i])
                {
                    effective.Add(routes[i].RouteId);
                    if (!routes[i].IsFree)
                    {
                        charge.Add(routes[i].RouteId);
                        result.FinalGoldVoucherAmount = checked(result.FinalGoldVoucherAmount + routes[i].TollGoldVoucherAmount);
                    }
                }
                else if (routes[i].IsFree && reachable[locationIds.Count + i]) prunedFree.Add(routes[i].RouteId);
            }
            var pruned = new List<string>();
            foreach (string id in confirmedRouteIds)
            {
                result.WorkUnits++;
                if (!keptRoutes[routeIndex[id]]) pruned.Add(id);
            }
            result.EffectiveRouteIds = effective.AsReadOnly();
            result.ChargeRouteIds = charge.AsReadOnly();
            result.PrunedRouteIds = pruned.AsReadOnly();
            result.PrunedFreeRouteIds = prunedFree.AsReadOnly();
            return result;
        }

        private static IReadOnlyList<string> Copy(IReadOnlyList<string> values)
        {
            var copy = new string[values.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = values[i];
            return Array.AsReadOnly(copy);
        }

        private static RouteGraphResult Fail(RouteGraphResult result, string diagnostic)
        {
            result.IsValid = false;
            result.HasFinalPlan = false;
            result.Diagnostic = diagnostic;
            return result;
        }

        private sealed class Edge
        {
            public int A;
            public int B;
            public int Other(int vertex) { return vertex == A ? B : A; }
        }

        private sealed class Graph
        {
            public readonly List<Edge> Edges = new List<Edge>();
            private readonly List<int>[] adjacency;

            public Graph(int count)
            {
                adjacency = new List<int>[count];
                for (int i = 0; i < count; i++) adjacency[i] = new List<int>();
            }

            public void AddEdge(int a, int b)
            {
                int edgeId = Edges.Count;
                Edges.Add(new Edge { A = a, B = b });
                adjacency[a].Add(edgeId);
                adjacency[b].Add(edgeId);
            }

            public bool[] Reachable(int start, RouteGraphResult result)
            {
                var found = new bool[adjacency.Length];
                var queue = new Queue<int>();
                found[start] = true;
                queue.Enqueue(start);
                while (queue.Count != 0)
                {
                    int vertex = queue.Dequeue();
                    result.WorkUnits++;
                    foreach (int edgeId in adjacency[vertex])
                    {
                        result.WorkUnits++;
                        int next = Edges[edgeId].Other(vertex);
                        if (found[next]) continue;
                        found[next] = true;
                        queue.Enqueue(next);
                    }
                }
                return found;
            }

            public bool[] KeepTerminalBlocks(int start, HashSet<int> targets, RouteGraphResult result)
            {
                var blocks = FindBlocks(start, result);
                var memberships = new List<int>[adjacency.Length];
                for (int i = 0; i < memberships.Length; i++) memberships[i] = new List<int>();
                for (int i = 0; i < blocks.Count; i++)
                {
                    var uniqueVertices = new HashSet<int>();
                    foreach (int edgeId in blocks[i])
                    {
                        result.WorkUnits++;
                        uniqueVertices.Add(Edges[edgeId].A);
                        uniqueVertices.Add(Edges[edgeId].B);
                    }
                    foreach (int vertex in uniqueVertices)
                    {
                        result.WorkUnits++;
                        memberships[vertex].Add(i);
                    }
                }
                var tree = new List<List<int>>();
                for (int i = 0; i < blocks.Count; i++) tree.Add(new List<int>());
                var representedBy = new int[adjacency.Length];
                for (int i = 0; i < representedBy.Length; i++)
                {
                    result.WorkUnits++;
                    representedBy[i] = -1;
                    if (memberships[i].Count == 1) representedBy[i] = memberships[i][0];
                    else if (memberships[i].Count > 1)
                    {
                        int cutNode = tree.Count;
                        representedBy[i] = cutNode;
                        tree.Add(new List<int>());
                        foreach (int block in memberships[i])
                        {
                            result.WorkUnits++;
                            tree[cutNode].Add(block);
                            tree[block].Add(cutNode);
                        }
                    }
                }
                int root = representedBy[start];
                if (root < 0) throw new InvalidOperationException("可达非起点目标应存在块割树。");
                var parent = new int[tree.Count];
                for (int i = 0; i < parent.Length; i++) parent[i] = -2;
                var order = new List<int>();
                var queue = new Queue<int>();
                parent[root] = -1;
                queue.Enqueue(root);
                while (queue.Count != 0)
                {
                    int current = queue.Dequeue();
                    result.WorkUnits++;
                    order.Add(current);
                    foreach (int next in tree[current])
                    {
                        result.WorkUnits++;
                        if (parent[next] != -2) continue;
                        parent[next] = current;
                        queue.Enqueue(next);
                    }
                }
                var retained = new bool[tree.Count];
                retained[root] = true;
                foreach (int target in targets)
                {
                    result.WorkUnits++;
                    if (representedBy[target] < 0) throw new InvalidOperationException("目标不在块割树中。");
                    retained[representedBy[target]] = true;
                }
                for (int i = order.Count - 1; i >= 0; i--)
                {
                    result.WorkUnits++;
                    int node = order[i];
                    if (retained[node] && parent[node] >= 0) retained[parent[node]] = true;
                }
                var keptEdges = new bool[Edges.Count];
                for (int block = 0; block < blocks.Count; block++)
                {
                    result.WorkUnits++;
                    if (!retained[block]) continue;
                    foreach (int edgeId in blocks[block])
                    {
                        result.WorkUnits++;
                        keptEdges[edgeId] = true;
                    }
                }
                return keptEdges;
            }

            // 迭代Tarjan：每条关联边按edgeId进栈一次，父边仅排除那个edgeId。
            private List<List<int>> FindBlocks(int start, RouteGraphResult result)
            {
                var discovery = new int[adjacency.Length];
                var low = new int[adjacency.Length];
                var parentEdge = new int[adjacency.Length];
                var nextNeighbor = new int[adjacency.Length];
                for (int i = 0; i < parentEdge.Length; i++) parentEdge[i] = -1;
                var vertexStack = new Stack<int>();
                var edgeStack = new Stack<int>();
                var blocks = new List<List<int>>();
                int time = 1;
                discovery[start] = low[start] = time;
                vertexStack.Push(start);
                while (vertexStack.Count != 0)
                {
                    int vertex = vertexStack.Peek();
                    result.WorkUnits++;
                    if (nextNeighbor[vertex] < adjacency[vertex].Count)
                    {
                        int edgeId = adjacency[vertex][nextNeighbor[vertex]++];
                        if (edgeId == parentEdge[vertex]) continue;
                        int next = Edges[edgeId].Other(vertex);
                        if (discovery[next] == 0)
                        {
                            parentEdge[next] = edgeId;
                            discovery[next] = low[next] = ++time;
                            edgeStack.Push(edgeId);
                            vertexStack.Push(next);
                        }
                        else if (discovery[next] < discovery[vertex])
                        {
                            edgeStack.Push(edgeId);
                            low[vertex] = Math.Min(low[vertex], discovery[next]);
                        }
                    }
                    else
                    {
                        vertexStack.Pop();
                        int edgeId = parentEdge[vertex];
                        if (edgeId < 0) continue;
                        int parent = Edges[edgeId].Other(vertex);
                        low[parent] = Math.Min(low[parent], low[vertex]);
                        if (low[vertex] < discovery[parent]) continue;
                        var block = new List<int>();
                        int popped;
                        do
                        {
                            result.WorkUnits++;
                            popped = edgeStack.Pop();
                            block.Add(popped);
                        } while (popped != edgeId);
                        blocks.Add(block);
                    }
                }
                return blocks;
            }
        }
    }
}
