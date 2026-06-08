using System;
using System.Collections.Generic;

namespace YC.Domain.Maps
{
    [Serializable]
    public sealed class MapPath
    {
        public List<string> LocationIds = new List<string>();
        public List<string> RouteIds = new List<string>();

        public int StepCount
        {
            get { return RouteIds.Count; }
        }

        public bool IsEmpty
        {
            get { return LocationIds.Count == 0; }
        }
    }
}
