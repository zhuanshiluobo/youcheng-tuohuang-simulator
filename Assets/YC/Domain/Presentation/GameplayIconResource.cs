using System;
using System.Security.Cryptography;

namespace YC.Domain.Presentation
{
    /// <summary>已冻结的包内显示资源。无Unity、磁盘或网络依赖；调用者不能改写原字节。</summary>
    public sealed class GameplayIconResource
    {
        private readonly byte[] bytes;
        public string OwnerPackId { get; }
        public string AssetKey { get; }
        public string RelativePath { get; }
        public string Sha256 { get; }
        public GameplayIconResource(string ownerPackId, string assetKey, string relativePath, byte[] content)
        {
            OwnerPackId = ownerPackId ?? throw new ArgumentNullException(nameof(ownerPackId));
            AssetKey = assetKey ?? throw new ArgumentNullException(nameof(assetKey));
            RelativePath = relativePath ?? throw new ArgumentNullException(nameof(relativePath));
            bytes = (byte[])(content ?? throw new ArgumentNullException(nameof(content))).Clone();
            using (var hash = SHA256.Create()) Sha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        public byte[] CopyBytes() => (byte[])bytes.Clone();
    }

    public interface IGameplayIconResourceCatalog
    {
        System.Collections.Generic.IEnumerable<GameplayIconResource> IconResources { get; }
        bool TryGetIconResource(string assetKey, out GameplayIconResource resource);
    }
    public interface IGameplayTextSourceCatalog
    {
        bool TryGetTemplateSource(string key, out string source);
        bool UsesStrictTemplateSyntax(string key);
    }
}
