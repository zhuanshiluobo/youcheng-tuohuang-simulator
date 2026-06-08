using System.Collections.Generic;

namespace YC.Domain.Maps
{
    public interface IMapQueryService
    {
        GameMapDefinition Map { get; }
        MapLocationDefinition GetLocation(string locationId);
        MapRouteDefinition GetRoute(string routeId);
        MapRouteDefinition FindRoute(string fromLocationId, string toLocationId);
        IReadOnlyList<MapLocationDefinition> GetAdjacentLocations(string locationId);
        MapRegionDefinition GetRegionForLocation(string locationId);
        bool CanDockCity(string locationId);
    }
}
