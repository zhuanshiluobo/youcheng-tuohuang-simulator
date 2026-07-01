using System;
using System.Collections.Generic;
using YC.Domain.Maps;

namespace YC.Presentation
{
    public sealed class ExplorePaymentRecipientSelectionController
    {
        private readonly Dictionary<string, int> recipientsByRouteId = new Dictionary<string, int>();
        private readonly List<ExplorePaymentChoice> choices = new List<ExplorePaymentChoice>();

        public IReadOnlyList<ExplorePaymentChoice> Choices
        {
            get { return choices; }
        }

        public IReadOnlyDictionary<string, int> RecipientsByRouteId
        {
            get { return recipientsByRouteId; }
        }

        public bool HasChoices
        {
            get { return choices.Count > 0; }
        }

        public void Clear()
        {
            recipientsByRouteId.Clear();
            choices.Clear();
        }

        public void BuildForPathWithPaymentKeys(
            MapPath path,
            Func<string, string> getPaymentKey,
            Func<string, bool> hasRouteInfluenceOwnedByLocalPlayer,
            Func<string, List<int>> getOpponentInfluenceOwnersOnRoute)
        {
            Clear();

            if (path == null)
            {
                return;
            }

            var paidPaymentKeys = new List<string>();
            for (var i = 0; i < path.RouteIds.Count; i++)
            {
                var routeId = path.RouteIds[i] ?? string.Empty;
                if (string.IsNullOrEmpty(routeId) ||
                    (hasRouteInfluenceOwnedByLocalPlayer != null && hasRouteInfluenceOwnedByLocalPlayer(routeId)))
                {
                    continue;
                }

                var paymentKey = getPaymentKey == null ? routeId : getPaymentKey(routeId);
                if (paidPaymentKeys.Contains(paymentKey))
                {
                    continue;
                }

                paidPaymentKeys.Add(paymentKey);
                var opponentOwners = getOpponentInfluenceOwnersOnRoute == null
                    ? null
                    : getOpponentInfluenceOwnersOnRoute(routeId);
                if (opponentOwners == null || opponentOwners.Count <= 0)
                {
                    continue;
                }

                choices.Add(new ExplorePaymentChoice(routeId, opponentOwners));
                recipientsByRouteId[routeId] = opponentOwners[0];
            }
        }

        public void BuildForPath(
            MapPath path,
            Func<string, bool> hasRouteInfluenceOwnedByLocalPlayer,
            Func<string, List<int>> getOpponentInfluenceOwnersOnRoute)
        {
            BuildForPathWithPaymentKeys(path, null, hasRouteInfluenceOwnedByLocalPlayer, getOpponentInfluenceOwnersOnRoute);
        }

        public bool SelectRecipient(string routeId, int recipientPlayerId)
        {
            if (string.IsNullOrEmpty(routeId))
            {
                return false;
            }

            for (var i = 0; i < choices.Count; i++)
            {
                var choice = choices[i];
                if (choice.RouteId != routeId || !choice.RecipientPlayerIds.Contains(recipientPlayerId))
                {
                    continue;
                }

                recipientsByRouteId[routeId] = recipientPlayerId;
                return true;
            }

            return false;
        }

        public string EncodeRecipients()
        {
            var encoded = string.Empty;
            for (var i = 0; i < choices.Count; i++)
            {
                var routeId = choices[i].RouteId;
                int recipientPlayerId;
                if (!recipientsByRouteId.TryGetValue(routeId, out recipientPlayerId))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(encoded))
                {
                    encoded += ";";
                }

                encoded += routeId + "=" + recipientPlayerId;
            }

            return encoded;
        }
    }
}
