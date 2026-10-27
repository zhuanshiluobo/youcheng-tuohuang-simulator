using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.State;

namespace YC.Application.Sessions
{
    [Serializable] public sealed class PaymentSourcePageRequest
    {
        public int ProtocolVersion = 1;
        public string RequestId = "";
        public PaymentSourceQuery Query;
    }
    [Serializable] public sealed class PaymentSourcePageResponse
    {
        public int ProtocolVersion = 1;
        public string RequestId = "", Reason = "";
        public PaymentSourceQuery Query;
        public PaymentSourceProjection Page;
    }
    public static class PaymentSourcePageProtocol
    {
        public const string Version = ResourcePaymentDraftHost.SourceQueryVersion;
        public static bool Valid(PaymentSourceQuery q) => q != null && q.PlayerId > 0 && q.StateRevision >= 0 &&
            q.PaymentGeneration > 0 && q.SourceIndex >= 0 && q.ItemOffset >= 0 &&
            new[] { q.OwnerEffectId, q.InteractionId, q.SnapshotId }.All(s => !string.IsNullOrEmpty(s) && s.Length <= 1024);
        public static string Id(PaymentSourceQuery q) => FacilityBuildQueryService.Hash(Version + "|" + JsonConvert.SerializeObject(q));
        internal static string Basis(PaymentSourceQuery q)
        { var copy = GameStateCloneService.DeepClone(q); copy.SourceIndex = 0; copy.ItemOffset = 0; return Id(copy); }
    }
    /// <summary>仅缓存同一次正式可见快照的分页；未收到或已失效的页返回null。</summary>
    public sealed class PaymentSourcePageCache
    {
        private string scope = "", basis = "";
        private readonly Dictionary<string, PaymentSourceProjection> pages = new Dictionary<string, PaymentSourceProjection>(StringComparer.Ordinal);
        private readonly HashSet<string> pending = new HashSet<string>(StringComparer.Ordinal);
        public void Reset() { scope = basis = ""; pages.Clear(); pending.Clear(); }
        public PaymentSourceProjection Read(string currentScope, PaymentSourceQuery q)
        {
            if (!PaymentSourcePageProtocol.Valid(q)) return null;
            string currentBasis = PaymentSourcePageProtocol.Basis(q);
            if (scope != currentScope || basis != currentBasis)
            { Reset(); scope = currentScope; basis = currentBasis; }
            return pages.TryGetValue(PaymentSourcePageProtocol.Id(q), out var page) ? GameStateCloneService.DeepClone(page) : null;
        }
        public bool Begin(string currentScope, PaymentSourceQuery q)
        { Read(currentScope, q); return PaymentSourcePageProtocol.Valid(q) && pending.Add(PaymentSourcePageProtocol.Id(q)); }
        public void Retry(PaymentSourceQuery q) { if (PaymentSourcePageProtocol.Valid(q)) pending.Remove(PaymentSourcePageProtocol.Id(q)); }
        public bool Accept(string currentScope, PaymentSourcePageResponse response)
        {
            var q = response?.Query;
            if (response?.ProtocolVersion != 1 || !PaymentSourcePageProtocol.Valid(q) || scope != currentScope ||
                basis != PaymentSourcePageProtocol.Basis(q) || response.RequestId != PaymentSourcePageProtocol.Id(q) ||
                !pending.Remove(response.RequestId)) return false;
            var p = response.Page;
            if (!string.IsNullOrEmpty(response.Reason) || p == null || p.ProtocolVersion != ResourcePaymentDraftHost.SourceQueryProtocolVersion ||
                p.PlayerId != q.PlayerId || p.StateRevision != q.StateRevision || p.PaymentGeneration != q.PaymentGeneration ||
                p.OwnerEffectId != q.OwnerEffectId || p.InteractionId != q.InteractionId || p.SnapshotId != q.SnapshotId ||
                p.SourceIndex != q.SourceIndex || p.ItemOffset != q.ItemOffset || p.SourceCount < 0 || p.Sources == null ||
                p.Sources.Count != (p.SourceCount == 0 ? 0 : 1) || p.Sources.Any(s => s?.Rule == null || s.AvailableItems == null ||
                    s.AvailableItems.Count > ResourcePaymentDraftHost.SourceQueryItemPageSize)) return false;
            pages[response.RequestId] = GameStateCloneService.DeepClone(p); return true;
        }
    }
    public sealed partial class GameSession
    {
        private EffectRegistry paymentSourceRegistry;
        public void ConfigurePaymentSourceQueries(EffectRegistry registry)
        {
            EnsureExecutionOwner();
            if (registry == null || paymentSourceRegistry != null && !ReferenceEquals(paymentSourceRegistry, registry))
                throw new InvalidOperationException("payment_query_registry_binding_invalid");
            paymentSourceRegistry = registry;
        }
        public PaymentSourcePageResponse QueryPaymentSourcePage(int authenticatedPlayerId, PaymentSourcePageRequest request)
        {
            EnsureExecutionOwner();
            var q = request?.Query;
            var response = new PaymentSourcePageResponse { RequestId = request?.RequestId ?? "", Query = GameStateCloneService.DeepClone(q), Reason = "payment_source_request_invalid" };
            if (commandsSuspended || request?.ProtocolVersion != 1 || !PaymentSourcePageProtocol.Valid(q) || q.PlayerId != authenticatedPlayerId ||
                request.RequestId != PaymentSourcePageProtocol.Id(q) || paymentSourceRegistry == null ||
                paymentSourceRegistry.ContentPackHash != State.ContentPackHash) return response;
            bool ok = new EffectTreeExecutor(State, paymentSourceRegistry).TryQueryPaymentSources(q, out var page, out var reason);
            response.Page = ok ? GameStateCloneService.DeepClone(page) : null; response.Reason = ok ? "" : reason; return response;
        }
    }
}
