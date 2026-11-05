using System;
using System.Collections.Generic;

namespace YC.Domain.State
{
    public static partial class GameStateCloneService
    {
        [ThreadStatic] private static bool fullJournalPublication;
        [ThreadStatic] private static JournalPrefixCopyScope journalPrefixCopy;
        public static IDisposable UseFullJournalPublicationForDiagnostics() => new FullJournalScope();
        private sealed class FullJournalScope : IDisposable
        {
            private readonly bool previous=fullJournalPublication;private bool closed;
            internal FullJournalScope(){fullJournalPublication=true;}
            public void Dispose(){if(closed)return;closed=true;fullJournalPublication=previous;}
        }
        // 只证明一次封闭内置执行前后已有前缀不变；不凭revision缓存校验结果。
        // 执行器须单独声明不改旧日志，任何未经审计的状态回调都会撤销此凭据。
        internal readonly struct JournalPublicationProof
        {
            private readonly GameState sourceRoot,targetRoot;
            private readonly EffectRuntimeState sourceRuntime,targetRuntime;
            internal readonly List<RuleJournalEntry> SourceJournal,TargetJournal;
            internal readonly int PrefixCount;
            private readonly int revision;
            internal JournalPublicationProof(GameState source,GameState target)
            {
                sourceRoot=source;targetRoot=target;sourceRuntime=source.EffectRuntime;targetRuntime=target.EffectRuntime;
                SourceJournal=sourceRuntime.Journal;TargetJournal=targetRuntime.Journal;PrefixCount=SourceJournal.Count;revision=sourceRuntime.StateRevision;
            }
            internal bool MatchesBeforeCopy(GameState source,GameState target) => !fullJournalPublication && sourceRoot!=null &&
                ReferenceEquals(sourceRoot,source) && ReferenceEquals(targetRoot,target) &&
                ReferenceEquals(sourceRuntime,source.EffectRuntime) && ReferenceEquals(targetRuntime,target.EffectRuntime) &&
                sourceRuntime.SchemaVersion==EffectRuntimeState.CurrentSchemaVersion && targetRuntime.SchemaVersion==sourceRuntime.SchemaVersion &&
                sourceRuntime.Status==EffectRuntimeStatus.Active && targetRuntime.Status==EffectRuntimeStatus.Active &&
                sourceRuntime.StateRevision>revision && targetRuntime.StateRevision==revision &&
                ReferenceEquals(SourceJournal,sourceRuntime.Journal) && ReferenceEquals(TargetJournal,targetRuntime.Journal) &&
                SourceJournal.Count>PrefixCount && TargetJournal.Count==PrefixCount;
            internal bool MatchesLists<T>(List<T> target,List<T> source) => !fullJournalPublication &&
                sourceRoot!=null && ReferenceEquals(TargetJournal,target) && ReferenceEquals(SourceJournal,source) &&
                ReferenceEquals(sourceRuntime,sourceRoot.EffectRuntime) && ReferenceEquals(targetRuntime,targetRoot.EffectRuntime) &&
                ReferenceEquals(SourceJournal,sourceRuntime.Journal) && ReferenceEquals(TargetJournal,targetRuntime.Journal) &&
                TargetJournal.Count==PrefixCount && SourceJournal.Count>PrefixCount;
        }
        internal static JournalPublicationProof CaptureJournalPublicationProof(GameState source,GameState target)
        {
            if(fullJournalPublication || source==null || target==null || ReferenceEquals(source,target) ||
                source.EffectRuntime?.Journal==null || target.EffectRuntime?.Journal==null ||
                source.EffectRuntime.Journal.GetType()!=typeof(List<RuleJournalEntry>) || target.EffectRuntime.Journal.GetType()!=typeof(List<RuleJournalEntry>) ||
                source.EffectRuntime.SchemaVersion!=EffectRuntimeState.CurrentSchemaVersion || target.EffectRuntime.SchemaVersion!=source.EffectRuntime.SchemaVersion ||
                source.EffectRuntime.Status!=EffectRuntimeStatus.Active || target.EffectRuntime.Status!=EffectRuntimeStatus.Active ||
                source.EffectRuntime.StateRevision!=target.EffectRuntime.StateRevision || source.EffectRuntime.Journal.Count!=target.EffectRuntime.Journal.Count ||
                ReferenceEquals(source.EffectRuntime.Journal,target.EffectRuntime.Journal))return default;
            return new JournalPublicationProof(source,target);
        }
        internal static IDisposable BeginJournalPrefixCopy(JournalPublicationProof proof) => new JournalPrefixCopyScope(proof);
        private sealed class JournalPrefixCopyScope : IDisposable
        {
            private readonly JournalPrefixCopyScope previous;
            internal JournalPublicationProof Proof;
            private bool closed;
            internal JournalPrefixCopyScope(JournalPublicationProof proof){Proof=proof;previous=journalPrefixCopy;journalPrefixCopy=this;}
            public void Dispose()
            {
                if(closed)return;closed=true;Proof=default;
                if(!ReferenceEquals(journalPrefixCopy,this))return;
                var restored=previous;while(restored!=null && restored.closed)restored=restored.previous;journalPrefixCopy=restored;
            }
        }
        private static bool TryCopyJournalPrefix<T>(List<T> target,List<T> source,DiagnosticPath path,HashSet<object> active) where T:class
        {
            var scope=journalPrefixCopy;
            if(scope==null || !scope.Proof.MatchesLists(target,source))return false;
            // 目标和源的精确列表对只在最终CopyTo期间获准；嵌套复制另一根仍走原路径。
            // 新尾部每项完整深复制，目标前缀和外部可能持有的既有对象保持原样。
            using var sample=MeasureDiagnostic("handler.copy.journal_prefix");
            if(source.Count>target.Capacity)target.Capacity=source.Count;
            for(int i=scope.Proof.PrefixCount;i<source.Count;i++)target.Add(CloneKnownDto(source[i],path.AtIndex(i),active));
            return true;
        }
    }
}
