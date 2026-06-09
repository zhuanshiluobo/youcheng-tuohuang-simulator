using System;
using System.Collections;
using System.Reflection;
using YC.Domain.State;

namespace YC.Domain.Influence
{
    public interface IInfluenceRoadCoverageQuery
    {
        bool IsRouteCoveredByRoad(GameState state, string routeId);
    }

    public sealed class NoInfluenceRoadCoverageQuery : IInfluenceRoadCoverageQuery
    {
        public bool IsRouteCoveredByRoad(GameState state, string routeId)
        {
            return false;
        }
    }

    public sealed class RuntimeStateRoadCoverageQuery : IInfluenceRoadCoverageQuery
    {
        public bool IsRouteCoveredByRoad(GameState state, string routeId)
        {
            if (state == null || state.Map == null || string.IsNullOrEmpty(routeId))
            {
                return false;
            }

            var mapState = state.Map;
            var mapStateType = mapState.GetType();
            var field = mapStateType.GetField("RoadRouteIds", BindingFlags.Instance | BindingFlags.Public);
            if (field != null)
            {
                return ContainsRouteId(field.GetValue(mapState), routeId);
            }

            var property = mapStateType.GetProperty("RoadRouteIds", BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return ContainsRouteId(property.GetValue(mapState, null), routeId);
            }

            return false;
        }

        private static bool ContainsRouteId(object value, string routeId)
        {
            var routes = value as IEnumerable;
            if (routes == null)
            {
                return false;
            }

            foreach (var item in routes)
            {
                if (string.Equals(item as string, routeId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
