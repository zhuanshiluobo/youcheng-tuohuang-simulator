using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Presentation;

namespace YC.Presentation.Workflows
{
    public enum EnterpriseSelectionMode { Company, Effect, InitialDepartment, SwitchDepartment, BrowseDepartment }

    // 表现合同，不是领域规则或 InteractionRequest 类型注册。
    public sealed class EnterpriseSelectionTarget
    {
        public string Id, CompanyId, EffectId, DepartmentId, BoardId;
        public string Label, Description, Cost, UnavailableReason;
        public GameplayTextRef[] Details = Array.Empty<GameplayTextRef>();
        public int Tier;
        public bool Available;
        // 企业板局部归一化矩形，原点在左上；只有正式特效候选提供该区域。
        public float X, Y, Width = 1, Height = 1;
        internal EnterpriseSelectionTarget Copy()
        {
            var copy = (EnterpriseSelectionTarget)MemberwiseClone();
            copy.Details = (Details ?? Array.Empty<GameplayTextRef>()).Select(value => value?.Clone()).ToArray();
            return copy;
        }
    }

    // 已激活科室的只读表现，不是待选科室或企业特效目标。
    public sealed class EnterpriseDepartmentProjection
    {
        public string ItemId, VisualKey, Label, Summary;
        public GameplayTextRef[] Details = Array.Empty<GameplayTextRef>();
        internal EnterpriseDepartmentProjection Copy()
        {
            var copy = (EnterpriseDepartmentProjection)MemberwiseClone();
            copy.Details = (Details ?? Array.Empty<GameplayTextRef>()).Select(value => value?.Clone()).ToArray();
            return copy;
        }
    }

    public sealed class EnterpriseBoardProjection
    {
        public string Id, VisualKey, Label;
        public int[] PlayerLevels;
        public int[] PlayerIds, PlayerColors;
        public int ViewerPlayerId;
        public string Summary;
        public GameplayTextRef[] Details = Array.Empty<GameplayTextRef>();
        public bool SupportsDepartmentBrowsing;
        public EnterpriseDepartmentProjection ActiveDepartment;
        internal EnterpriseBoardProjection Copy()
        {
            var copy = (EnterpriseBoardProjection)MemberwiseClone();
            copy.PlayerLevels = PlayerLevels == null ? null : (int[])PlayerLevels.Clone();
            copy.PlayerIds = PlayerIds == null ? null : (int[])PlayerIds.Clone();
            copy.PlayerColors = PlayerColors == null ? null : (int[])PlayerColors.Clone();
            copy.ActiveDepartment = ActiveDepartment?.Copy();
            copy.Details = (Details ?? Array.Empty<GameplayTextRef>()).Select(value => value?.Clone()).ToArray();
            return copy;
        }
    }

    public sealed class EnterpriseSelectionProjection
    {
        public string RequestId, Context, CurrentDepartmentId;
        public string GameId, CandidateSetId, OwnerEffectId, RequestContract;
        public int Revision;
        public int ViewerPlayerId;
        public int CandidateSetVersion;
        public int MinSelections = 1, MaxSelections = 1;
        public bool ReadOnly;
        public EnterpriseSelectionMode Mode;
        public bool CanCancel, SubmissionAvailable;
        public string UnavailableReason;
        public EnterpriseBoardProjection FixedCompany;
        public EnterpriseBoardProjection[] Boards = Array.Empty<EnterpriseBoardProjection>();
        public EnterpriseSelectionTarget[] Targets = Array.Empty<EnterpriseSelectionTarget>();
        internal EnterpriseSelectionProjection Copy()
        {
            var copy = (EnterpriseSelectionProjection)MemberwiseClone();
            copy.Boards = Boards.Select(b => b.Copy()).ToArray();
            copy.Targets = Targets.Select(t => t.Copy()).ToArray();
            copy.FixedCompany = FixedCompany?.Copy();
            return copy;
        }
    }

    public sealed class EnterpriseSelectionIntent
    {
        public string RequestId { get; internal set; }
        public string GameId { get; internal set; }
        public int Revision { get; internal set; }
        public EnterpriseSelectionMode Mode { get; internal set; }
        public string TargetId { get; internal set; }
        public string CompanyId { get; internal set; }
        public string EffectId { get; internal set; }
        public string DepartmentId { get; internal set; }
        public int Tier { get; internal set; }
        public IReadOnlyList<string> TargetIds { get; internal set; }
        public int CandidateSetVersion { get; internal set; }
        internal long Generation;
    }

    /// <summary>仅管理草稿和回包身份。支付、公共科室、等级及奖励始终由正式宿主负责。</summary>
    public sealed class EnterpriseSelectionDraft
    {
        private EnterpriseSelectionProjection projection;
        private long generation;
        private EnterpriseSelectionIntent pending;
        private readonly List<string> selectedIds = new List<string>();
        public IReadOnlyList<string> SelectedIds => selectedIds.AsReadOnly();
        public bool IsSelected(string id) => selectedIds.Contains(id);
        public EnterpriseSelectionProjection Projection => projection;
        public string SelectedId { get; private set; }
        public string InspectedId { get; private set; }
        public bool IsPending => pending != null;
        public EnterpriseSelectionTarget Selected => Find(SelectedId);
        public EnterpriseSelectionTarget Inspected => Find(InspectedId);
        public bool CanConfirm => projection != null && projection.SubmissionAvailable &&
            !string.IsNullOrEmpty(projection.RequestId) && !IsPending && !projection.ReadOnly &&
            projection.Mode != EnterpriseSelectionMode.BrowseDepartment &&
            selectedIds.Count >= projection.MinSelections && selectedIds.Count <= projection.MaxSelections &&
            selectedIds.All(id => IsAvailable(Find(id)));

        public bool Refresh(EnterpriseSelectionProjection next)
        {
            if (next == null) { Clear(); return true; }
            Validate(next);
            if (projection != null && projection.GameId == next.GameId && projection.ViewerPlayerId == next.ViewerPlayerId &&
                projection.RequestId == next.RequestId && next.Revision < projection.Revision)
                return false;
            bool same = projection != null && projection.RequestId == next.RequestId && projection.Mode == next.Mode &&
                projection.ViewerPlayerId == next.ViewerPlayerId && projection.GameId == next.GameId && projection.OwnerEffectId == next.OwnerEffectId;
            if (!same) { generation++; pending = null; }
            if (!same) { SelectedId = null; InspectedId = null; selectedIds.Clear(); }
            // 宿主复用或修改它自己的投影对象时，不得回写隐藏中的草稿版本和资格。
            projection = next.Copy();
            selectedIds.RemoveAll(id => !IsAvailable(Find(id)));
            if (selectedIds.Count > projection.MaxSelections) selectedIds.RemoveRange(projection.MaxSelections, selectedIds.Count - projection.MaxSelections);
            if (!IsAvailable(Selected)) SelectedId = null;
            if (Find(InspectedId) == null) InspectedId = null;
            if ((projection.Mode == EnterpriseSelectionMode.InitialDepartment || projection.Mode == EnterpriseSelectionMode.SwitchDepartment ||
                 projection.Mode == EnterpriseSelectionMode.BrowseDepartment) && Inspected?.DepartmentId == projection.CurrentDepartmentId)
                InspectedId = null;
            return true;
        }

        public static bool SameContract(EnterpriseSelectionProjection left, EnterpriseSelectionProjection right) =>
            left != null && right != null && left.GameId == right.GameId && left.ViewerPlayerId == right.ViewerPlayerId &&
            left.RequestId == right.RequestId && left.Mode == right.Mode && left.OwnerEffectId == right.OwnerEffectId &&
            left.CandidateSetId == right.CandidateSetId && left.CandidateSetVersion == right.CandidateSetVersion &&
            left.RequestContract == right.RequestContract && left.MinSelections == right.MinSelections &&
            left.MaxSelections == right.MaxSelections && left.CanCancel == right.CanCancel && left.ReadOnly == right.ReadOnly &&
            left.CurrentDepartmentId == right.CurrentDepartmentId && left.Targets.Length == right.Targets.Length &&
            left.Targets.Zip(right.Targets, (a,b) => a.Id == b.Id && a.Available == b.Available && a.Cost == b.Cost).All(value => value);

        public void Inspect(string id)
        {
            if (Find(id) != null) InspectedId = id;
        }

        public bool Select(string id)
        {
            if (IsPending) return false;
            var target = Find(id);
            if (target == null) return false;
            InspectedId = id;
            if (projection.ReadOnly) return false;
            if (!IsAvailable(target)) { selectedIds.Clear(); SelectedId = null; return false; }
            if (selectedIds.Contains(id)) selectedIds.Remove(id);
            else
            {
                if (projection.MaxSelections == 1) selectedIds.Clear();
                if (selectedIds.Count < projection.MaxSelections) selectedIds.Add(id);
            }
            SelectedId = selectedIds.LastOrDefault();
            return SelectedId != null;
        }

        public bool TryBeginSubmit(out EnterpriseSelectionIntent intent)
        {
            intent = null;
            if (!CanConfirm) return false;
            var target = Selected;
            intent = new EnterpriseSelectionIntent
            {
                RequestId = projection.RequestId, GameId = projection.GameId, Revision = projection.Revision, Mode = projection.Mode,
                TargetId = target.Id,
                CompanyId = projection.Mode == EnterpriseSelectionMode.Company || projection.Mode == EnterpriseSelectionMode.Effect ? target.CompanyId : null,
                EffectId = projection.Mode == EnterpriseSelectionMode.Effect ? target.EffectId : null,
                DepartmentId = projection.Mode == EnterpriseSelectionMode.InitialDepartment || projection.Mode == EnterpriseSelectionMode.SwitchDepartment ? target.DepartmentId : null,
                Tier = projection.Mode == EnterpriseSelectionMode.Effect ? target.Tier : 0, Generation = generation,
                TargetIds = selectedIds.ToArray(), CandidateSetVersion = projection.CandidateSetVersion
            };
            pending = intent;
            return true;
        }

        // 返回 false 表示迟到或重复回包；调用方不得据此关闭当前页。
        public bool Complete(EnterpriseSelectionIntent intent)
        {
            if (intent == null || !ReferenceEquals(intent, pending) || intent.Generation != generation ||
                projection == null || intent.GameId != projection.GameId || intent.RequestId != projection.RequestId)
                return false;
            pending = null;
            return true;
        }

        public bool IsAvailable(EnterpriseSelectionTarget target) => target != null && target.Available &&
            projection.Mode != EnterpriseSelectionMode.BrowseDepartment &&
            (projection.Mode != EnterpriseSelectionMode.SwitchDepartment && projection.Mode != EnterpriseSelectionMode.InitialDepartment ||
             target.DepartmentId != projection.CurrentDepartmentId);

        public void Clear()
        {
            generation++; pending = null; projection = null; SelectedId = null; InspectedId = null; selectedIds.Clear();
        }

        private EnterpriseSelectionTarget Find(string id) => string.IsNullOrEmpty(id) || projection == null
            ? null : projection.Targets.FirstOrDefault(t => t.Id == id);

        private static void Validate(EnterpriseSelectionProjection value)
        {
            if (value.Boards == null || value.Targets == null) throw new ArgumentException("候选投影不能为空。");
            if (!Enum.IsDefined(typeof(EnterpriseSelectionMode), value.Mode)) throw new ArgumentException("未知选择模式。");
            if (value.Mode == EnterpriseSelectionMode.BrowseDepartment &&
                (!value.ReadOnly || value.SubmissionAvailable || value.Targets.Any(target => target == null || target.Available)))
                throw new ArgumentException("公共科室浏览只能使用不可提交的只读投影。");
            var boards = new HashSet<string>(StringComparer.Ordinal);
            foreach (var board in value.Boards)
                if (board == null || string.IsNullOrEmpty(board.Id) || !boards.Add(board.Id))
                    throw new ArgumentException("企业／科室板必须具有唯一稳定 ID。");
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in value.Targets)
            {
                if (target == null || string.IsNullOrEmpty(target.Id) || !targets.Add(target.Id) || !boards.Contains(target.BoardId))
                    throw new ArgumentException("选择目标缺少唯一 ID 或对应板。");
                bool effect = value.Mode == EnterpriseSelectionMode.Effect;
                bool company = value.Mode == EnterpriseSelectionMode.Company;
                if ((effect || company) && string.IsNullOrEmpty(target.CompanyId) ||
                    effect && (string.IsNullOrEmpty(target.EffectId) || target.Tier < 1 ||
                    !(target.Width > 0 && target.Height > 0 && target.X >= 0 && target.Y >= 0 &&
                      target.X + target.Width <= 1 && target.Y + target.Height <= 1)) ||
                    !effect && !company && string.IsNullOrEmpty(target.DepartmentId))
                    throw new ArgumentException("选择目标与当前模式不匹配。");
            }
        }
    }
}
