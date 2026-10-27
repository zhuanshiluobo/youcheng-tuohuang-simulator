using System;
using System.Collections.Generic;
using Mirror;
using Newtonsoft.Json;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Infrastructure.Multiplayer
{
    public sealed partial class MirrorCommandTransport
    {
        private readonly PaymentSourcePageCache paymentPages = new PaymentSourcePageCache();
        private readonly Dictionary<string, float> paymentPageRequestedAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private string paymentPageScope = "";
        public event Action PaymentSourcePageAvailable;
        private string PaymentPageScope => transportGeneration + "|" + simulationOptions?.GameId + "|" +
            simulationOptions?.SimulationEpoch + "|" + simulationOptions?.MembershipVersion + "|" + clientConnectionToken;
        public PaymentSourceProjection ReadPaymentSourcePage(PaymentSourceQuery query)
        {
            if (!initialized || simulationCoordinator == null || simulationCoordinator.IsStopped || simulationCoordinator.RequiresRecovery ||
                IsRecoveringConfirmedPrefix || query?.PlayerId != localPlayerId || !PaymentSourcePageProtocol.Valid(query) ||
                mode == LaunchMode.Client && !NetworkClient.isConnected) return null;
            string scope = PaymentPageScope;
            if (paymentPageScope != scope)
            { paymentPages.Reset(); paymentPageRequestedAt.Clear(); paymentPageScope = scope; }
            var page = paymentPages.Read(scope, query); if (page != null) return page;
            string id = PaymentSourcePageProtocol.Id(query); float now = ReadUnscaledTime();
            if (paymentPageRequestedAt.TryGetValue(id, out float previous) && now - previous < 3f) return null;
            paymentPages.Retry(query);
            if (!paymentPages.Begin(scope, query)) return null;
            paymentPageRequestedAt[id] = now;
            var request = new PaymentSourcePageRequest { RequestId = id, Query = GameStateCloneService.DeepClone(query) };
            var coordinator = simulationCoordinator;
            if (mode == LaunchMode.Host)
            {
                bool queued = coordinator.TryQueryPaymentSourcePage(localPlayerId, request, result => {
                    if (!ReferenceEquals(coordinator, simulationCoordinator) || scope != PaymentPageScope) return;
                    if (paymentPages.Accept(scope, result)) PaymentSourcePageAvailable?.Invoke();
                });
                if (!queued) paymentPages.Retry(query);
            }
            else SendSimulationClient("payment-source-query", request);
            return paymentPages.Read(scope, query);
        }
        private void ReceivePaymentSourceQuery(NetworkConnectionToClient connection, int authenticatedPlayer, string token, string json)
        {
            if (json == null || json.Length > 8192) return;
            PaymentSourcePageRequest request;
            try { request = JsonConvert.DeserializeObject<PaymentSourcePageRequest>(json, new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }); }
            catch (JsonException) { return; }
            if (request?.ProtocolVersion != 1 || !PaymentSourcePageProtocol.Valid(request.Query) || request.Query.PlayerId != authenticatedPlayer ||
                request.RequestId != PaymentSourcePageProtocol.Id(request.Query)) return;
            var coordinator = simulationCoordinator; long generation = transportGeneration;
            coordinator.TryQueryPaymentSourcePage(authenticatedPlayer, request, result => {
                if (generation != transportGeneration || !ReferenceEquals(coordinator, simulationCoordinator) || connection == null ||
                    !bindings.TryGetPlayer(connection.connectionId, out int currentPlayer) || currentPlayer != authenticatedPlayer ||
                    !checkpointConnectionTokens.TryGetValue(connection.connectionId, out string currentToken) || currentToken != token) return;
                SendSimulationHost(connection, "payment-source-page", result);
            });
        }
        private void ReceivePaymentSourcePage(string json)
        {
            PaymentSourcePageResponse response;
            try { response = JsonConvert.DeserializeObject<PaymentSourcePageResponse>(json); }
            catch (JsonException) { return; }
            if (response?.Query?.PlayerId != localPlayerId) return;
            if (paymentPages.Accept(PaymentPageScope, response)) PaymentSourcePageAvailable?.Invoke();
        }
    }
}
