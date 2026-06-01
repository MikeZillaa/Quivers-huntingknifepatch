using AttributeRenderingLibrary;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace QuiversAndSheaths;

internal static class VariantTextureMatcher
{
    public static Dictionary<string, CompositeTexture> GetMatchingTextures(Variants variants, Dictionary<string, Dictionary<string, CompositeTexture>>? texturesByType)
    {
        Dictionary<string, CompositeTexture> result = new();
        if (texturesByType == null || texturesByType.Count == 0)
        {
            return result;
        }

        List<string> variantCodes = variants.GetAsStringArray();
        foreach ((string key, Dictionary<string, CompositeTexture> textures) in texturesByType)
        {
            if (!MatchesAllClauses(key, variantCodes))
            {
                continue;
            }

            foreach ((string textureCode, CompositeTexture texture) in textures)
            {
                result[textureCode] = texture;
            }
        }

        return result;
    }

    public static void BakeVariantTextures(
        ICoreClientAPI? clientApi,
        UniversalShapeTextureSource textureSource,
        Variants variants,
        Dictionary<string, Dictionary<string, CompositeTexture>>? texturesByType,
        Dictionary<string, AssetLocation>? prefixedTextureCodes = null,
        string overlayPrefix = "")
    {
        if (clientApi == null)
        {
            return;
        }

        foreach ((string textureCode, CompositeTexture texture) in GetMatchingTextures(variants, texturesByType))
        {
            CompositeTexture ctex = BakeTexture(clientApi, variants, texture);
            if (prefixedTextureCodes != null && prefixedTextureCodes.ContainsKey(textureCode))
            {
                textureSource.textures[overlayPrefix + textureCode] = ctex;
            }
            else
            {
                textureSource.textures[textureCode] = ctex;
            }
        }
    }

    public static CompositeTexture BakeTexture(ICoreAPI api, Variants variants, CompositeTexture texture)
    {
        CompositeTexture ctex = variants.ReplacePlaceholders(texture.Clone());
        if (!api.Assets.Exists(ctex.Base.CopyWithPathPrefixAndAppendixOnce("textures/", ".png")))
        {
            ctex.Base.Path = "unknown";
            ctex.Base.Domain = "game";
        }
        ctex.Bake(api.Assets);

        return ctex;
    }

    private static bool MatchesAllClauses(string key, List<string> variantCodes)
    {
        string[] clauses = key.Contains("::", StringComparison.Ordinal)
            ? key.Split("::")
            : [key];

        return clauses.All(clause => variantCodes.Any(variant => WildcardUtil.Match(clause, variant)));
    }
}
