using System;
using System.Globalization;
using System.Text;
using YC.Domain.Presentation;

namespace YC.Application.Presentation
{
    public enum LegacyGameplayTextInputKind { Literal, TextKey }

    /// <summary>旧入口显式选择文本或 key；不根据汉字、冒号、卡名猜测含义。</summary>
    public sealed class LegacyGameplayTextAdapter
    {
        private readonly GameplayTextResolver resolver;
        public LegacyGameplayTextAdapter(GameplayTextResolver resolver) { this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver)); }
        public GameplayTextDocument Resolve(string value, LegacyGameplayTextInputKind kind, GameplayTextContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (kind == LegacyGameplayTextInputKind.TextKey) return resolver.Resolve(new GameplayTextRef(value), context);
            if (kind != LegacyGameplayTextInputKind.Literal) throw new ArgumentOutOfRangeException(nameof(kind));
            value = value ?? "";
            var identity = new StringBuilder(); GameplayTextResolver.Field(identity, "legacy_literal");
            GameplayTextResolver.Field(identity, context.ContentHash); GameplayTextResolver.Field(identity, context.Locale);
            GameplayTextResolver.Field(identity, context.VisibilityScopeId); GameplayTextResolver.Field(identity, context.ViewerPlayerId.ToString(CultureInfo.InvariantCulture));
            GameplayTextResolver.Field(identity, value);
            return new GameplayTextDocument(new[] { new TextRun(value) }, GameplayTextResolver.Hash(identity.ToString()));
        }
    }
}
