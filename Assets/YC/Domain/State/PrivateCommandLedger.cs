using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace YC.Domain.State
{
    /// <summary>仅保存成功提交的身份和摘要；不保存命令正文或玩家选择。</summary>
    [Serializable]
    public sealed class PrivateCommandLedger
    {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        public string GameId = string.Empty, ContentPackHash = string.Empty;
        public int BaseRevision;
        public long CommittedSequence;
        public List<int> PlayerIds = new List<int>();
        public List<PrivateCommandReceipt> Receipts = new List<PrivateCommandReceipt>();
    }

    [Serializable]
    public sealed class PrivateCommandReceipt
    {
        public string CommandId = string.Empty, ProposalDigest = string.Empty, OperationWindowId = string.Empty;
        public int PlayerId, WindowGeneration, StateRevision;
        public long SubmissionSequence, GlobalSequence;
    }

    /// <summary>恢复时核验完整、连续的防重历史；已提交记录不会因局内操作次数而被丢弃。</summary>
    public static class PrivateCommandLedgerPolicy
    {
        private static bool Digest(string value) => value != null && value.Length == 64 &&
            value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');

        public static bool TryValidate(GameState state, out string reason)
        {
            reason = "private_command_ledger_invalid";
            var ledger = state?.PrivateCommandLedger;
            if (ledger == null)
            {
                if (state == null || state.RandomRuntime?.AlgorithmVersion == YC.Domain.Rules.GameplayRandomRuntime.PrivateAlgorithmVersion) return false;
                reason = string.Empty; return true;
            }
            if (ledger.Version != PrivateCommandLedger.CurrentVersion || state.SetupRecipe?.Preparation == null ||
                string.IsNullOrWhiteSpace(state.GameId) || string.IsNullOrWhiteSpace(state.ContentPackHash) ||
                ledger.GameId != state.GameId || ledger.ContentPackHash != state.ContentPackHash || state.EffectRuntime == null ||
                state.RandomRuntime?.AlgorithmVersion != YC.Domain.Rules.GameplayRandomRuntime.PrivateAlgorithmVersion ||
                !YC.Domain.Rules.GameplayRandomRuntime.TryValidate(state.RandomRuntime, state.GameId, state.EventDeckSeed, out _) ||
                ledger.BaseRevision < 0 || ledger.BaseRevision > state.EffectRuntime.StateRevision ||
                state.Players == null || state.Players.Any(player => player == null || player.PlayerId <= 0) ||
                ledger.PlayerIds == null || ledger.PlayerIds.Count < 2 || ledger.PlayerIds.Count > 4 ||
                ledger.PlayerIds.Any(player => player <= 0) || ledger.PlayerIds.Distinct().Count() != ledger.PlayerIds.Count ||
                !ledger.PlayerIds.OrderBy(player => player).SequenceEqual(state.Players.Select(player => player.PlayerId).OrderBy(player => player)) ||
                ledger.Receipts == null || ledger.CommittedSequence != ledger.Receipts.Count) return false;
            var next = ledger.PlayerIds.ToDictionary(player => player, _ => 1L);
            var commandIds = new HashSet<string>(StringComparer.Ordinal);
            long global = 0;
            int revision = ledger.BaseRevision;
            foreach (var receipt in ledger.Receipts)
            {
                if (receipt == null || string.IsNullOrWhiteSpace(receipt.CommandId) || receipt.CommandId.Length > 256 ||
                    !commandIds.Add(receipt.CommandId) || !Digest(receipt.ProposalDigest) ||
                    string.IsNullOrWhiteSpace(receipt.OperationWindowId) || receipt.OperationWindowId.Length > 256 ||
                    receipt.WindowGeneration <= 0 || !next.TryGetValue(receipt.PlayerId, out long expected) ||
                    receipt.SubmissionSequence != expected || receipt.GlobalSequence != ++global ||
                    receipt.StateRevision < revision || receipt.StateRevision > state.EffectRuntime.StateRevision) return false;
                next[receipt.PlayerId] = expected + 1;
                revision = receipt.StateRevision;
            }
            if (revision != state.EffectRuntime.StateRevision) return false;
            reason = string.Empty;
            return true;
        }

        public static bool HasRawFields(JObject state)
        {
            if (state == null) return false;
            var token = state.Property("PrivateCommandLedger");
            if (token == null)
            {
                var random = state["RandomRuntime"];
                // JSON null 是 JValue，并非 C# null；旧状态允许没有随机运行时，私密档仍必须带防重账本。
                return random == null || random.Type == JTokenType.Null || random is JObject value &&
                    (string)value["AlgorithmVersion"] != YC.Domain.Rules.GameplayRandomRuntime.PrivateAlgorithmVersion;
            }
            if (!(token.Value is JObject ledger) || ledger["Version"]?.Type != JTokenType.Integer ||
                ledger["GameId"]?.Type != JTokenType.String || ledger["ContentPackHash"]?.Type != JTokenType.String ||
                ledger["BaseRevision"]?.Type != JTokenType.Integer || ledger["CommittedSequence"]?.Type != JTokenType.Integer || !(ledger["PlayerIds"] is JArray players) ||
                players.Any(value => value.Type != JTokenType.Integer) || !(ledger["Receipts"] is JArray receipts)) return false;
            return receipts.All(value => value is JObject receipt &&
                receipt["CommandId"]?.Type == JTokenType.String && receipt["ProposalDigest"]?.Type == JTokenType.String &&
                receipt["OperationWindowId"]?.Type == JTokenType.String && receipt["PlayerId"]?.Type == JTokenType.Integer &&
                receipt["WindowGeneration"]?.Type == JTokenType.Integer && receipt["StateRevision"]?.Type == JTokenType.Integer &&
                receipt["SubmissionSequence"]?.Type == JTokenType.Integer && receipt["GlobalSequence"]?.Type == JTokenType.Integer);
        }
    }
}
