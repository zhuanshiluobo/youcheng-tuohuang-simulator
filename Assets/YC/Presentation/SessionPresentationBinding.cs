using YC.Application.Sessions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Presentation
{
    // 仅绑定正式装配的只读展示与当前会话身份，不读取/缓存玩家权限或可变实体索引。
    internal sealed class SessionPresentationBinding
    {
        private readonly GameSession session;
        private readonly string gameId;
        internal IActiveContentPresentation Content { get; }
        private SessionPresentationBinding(IActiveContentPresentation content, GameSession session, string gameId)
        { Content = content; this.session = session; this.gameId = gameId; }
        internal static SessionPresentationBinding Create(IActiveContentPresentation content, GameSession session,
            string gameId, GameRuleConfiguration configuration, string activeHash, string installedHash)
        {
            return content != null && session != null && !string.IsNullOrEmpty(gameId) &&
                content.MatchesConfiguration(configuration, activeHash, installedHash)
                ? new SessionPresentationBinding(content, session, gameId) : null;
        }
        internal bool Matches(GameSession currentSession, GameStateView view, string installedHash) =>
            ReferenceEquals(session, currentSession) && view != null && view.GameId == gameId &&
            Content.MatchesConfiguration(view.RuleConfiguration, view.ContentPackHash, installedHash);
    }
}
