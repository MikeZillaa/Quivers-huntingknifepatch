using AttributeRenderingLibrary;
using System.Text;
using VsOrderedDictionary = Vintagestory.API.Datastructures.OrderedDictionary<string, Vintagestory.API.Common.CompositeShape>;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace QuiversAndSheaths;

public class ShapeTexturesFromAttributes : CollectibleBehavior, IContainedMeshSource, IShapeTexturesFromAttributes, IAttachableToEntity
{
    private const string StoredOverlayTexturePrefix = "stored_";

    public Dictionary<string, List<object>> NameByType { get; protected set; } = new();
    public Dictionary<string, List<object>> DescriptionByType { get; protected set; } = new();
    public Dictionary<string, CompositeShape> ShapeByType { get; protected set; } = new();
    public Dictionary<string, Dictionary<string, CompositeTexture>> TexturesByType { get; protected set; } = new();
    public Dictionary<string, VsOrderedDictionary> AttachedShapeBySlotCodeByType { get; protected set; } = new();
    public Dictionary<string, string> CategoryCodeByType { get; protected set; } = new();
    public Dictionary<string, string[]> DisableElementsByType { get; protected set; } = new();
    public Dictionary<string, string[]> KeepElementsByType { get; protected set; } = new();
    public bool AddOverlayPrefix { get; protected set; } = true;
    public bool OnlyWhenWorn { get; protected set; } = false;
    public bool OnlyWhenNotWorn { get; protected set; } = false;
    public bool RenderStoredStackOverlay { get; protected set; } = false;
    public StoredStackOverlayConfig StoredStackOverlay { get; protected set; } = new();

    Dictionary<string, CompositeShape> IShapeTexturesFromAttributes.shapeByType => ShapeByType;
    Dictionary<string, Dictionary<string, CompositeTexture>> IShapeTexturesFromAttributes.texturesByType => TexturesByType;

    private IAttachableToEntity? _attachable;
    private ICoreAPI? _api;
    private ICoreClientAPI? _clientApi;

    public ShapeTexturesFromAttributes(CollectibleObject collObj) : base(collObj) { }

    public override void OnLoaded(ICoreAPI api)
    {
        _clientApi = api as ICoreClientAPI;
        _api = api;
        _attachable = IAttachableToEntity.FromAttributes(collObj);
    }

    public override void Initialize(JsonObject properties)
    {
        base.Initialize(properties);

        if (properties != null)
        {
            NameByType = properties["name"].AsObject<Dictionary<string, List<object>>>();
            DescriptionByType = properties["description"].AsObject<Dictionary<string, List<object>>>();

            ShapeByType = properties["shape"].AsObject<Dictionary<string, CompositeShape>>();
            TexturesByType = properties["textures"].AsObject<Dictionary<string, Dictionary<string, CompositeTexture>>>();

            AttachedShapeBySlotCodeByType = properties["attachedShapeBySlotCode"].AsObject<Dictionary<string, VsOrderedDictionary>>();
            CategoryCodeByType = properties["categoryCode"].AsObject<Dictionary<string, string>>();
            DisableElementsByType = properties["disableElements"].AsObject<Dictionary<string, string[]>>();
            KeepElementsByType = properties["keepElements"].AsObject<Dictionary<string, string[]>>();
            AddOverlayPrefix = properties["addOverlayPrefix"].AsBool(true);

            OnlyWhenWorn = properties["onlyWhenWorn"].AsBool(false);
            OnlyWhenNotWorn = properties["onlyWhenNotWorn"].AsBool(false);
            RenderStoredStackOverlay = properties["renderStoredStackOverlay"].AsBool(false);
            StoredStackOverlay = properties["storedStackOverlay"].AsObject<StoredStackOverlayConfig>() ?? new();
        }
    }

    public override void OnUnloaded(ICoreAPI api)
    {
        Dictionary<string, MultiTextureMeshRef> meshRefs = ObjectCacheUtil.TryGet<Dictionary<string, MultiTextureMeshRef>>(api, "AttributeRenderingLibrary_BehaviorShapeTexturesFromAttributes_MeshRefs");
        meshRefs?.Foreach(meshRef => meshRef.Value?.Dispose());
        ObjectCacheUtil.Delete(api, "AttributeRenderingLibrary_BehaviorShapeTexturesFromAttributes_MeshRefs");
    }

    public override void OnBeforeRender(ICoreClientAPI clientApi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        Dictionary<string, MultiTextureMeshRef> meshRefs = ObjectCacheUtil.GetOrCreate(clientApi, "AttributeRenderingLibrary_BehaviorShapeTexturesFromAttributes_MeshRefs", () => new Dictionary<string, MultiTextureMeshRef>());

        string key = GetMeshCacheKey(itemstack);

        if (!meshRefs.TryGetValue(key, out MultiTextureMeshRef meshref))
        {
            MeshData mesh = GetOrCreateMesh(itemstack, clientApi.ItemTextureAtlas);
            meshref = clientApi.Render.UploadMultiTextureMesh(mesh);
            meshRefs[key] = meshref;
        }

        renderinfo.ModelRef = meshref;
        renderinfo.NormalShaded = true;

        base.OnBeforeRender(clientApi, itemstack, target, ref renderinfo);
    }

    public override void GetHeldItemName(StringBuilder sb, ItemStack itemStack)
    {
        if (NameByType == null || !NameByType.Any())
        {
            return;
        }

        Variants variants = Variants.FromStack(itemStack);
        variants.FindByVariant(NameByType, out List<object> _langKeys);

        string name = variants.GetName(_langKeys);
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        sb.Clear();
        sb.Append(name);
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        if (DescriptionByType == null || !DescriptionByType.Any())
        {
            return;
        }

        Variants variants = Variants.FromStack(inSlot.Itemstack);
        variants.FindByVariant(DescriptionByType, out List<object> _langKeys);
        variants.GetDescription(dsc, _langKeys);
        variants.GetDebugDescription(dsc, withDebugInfo);
    }

    public virtual MeshData GetOrCreateMesh(ItemStack itemstack, ITextureAtlasAPI targetAtlas)
    {
        MeshData mesh = RenderExtensions.GenEmptyMesh();

        Variants variants = Variants.FromStack(itemstack);
        variants.FindByVariant(ShapeByType, out CompositeShape ucshape);
        ucshape ??= itemstack.Item.Shape;

        if (ucshape == null) return mesh;

        CompositeShape rcshape = variants.ReplacePlaceholders(ucshape.Clone());
        rcshape.Base = rcshape.Base.CopyWithPathPrefixAndAppendixOnce("shapes/", ".json");

        Shape? shape = _clientApi?.Assets.TryGet(rcshape.Base)?.ToObject<Shape>();
        if (shape == null) return mesh;

        UniversalShapeTextureSource stexSource = new(_clientApi, targetAtlas, shape, rcshape.Base.ToString());
        Dictionary<string, AssetLocation>? prefixedTextureCodes = null;
        string overlayPrefix = "";

        if (rcshape.Overlays != null && rcshape.Overlays.Length > 0)
        {
            overlayPrefix = GetMeshCacheKey(itemstack);
            prefixedTextureCodes = ShapeOverlayHelper.AddOverlays(_clientApi, AddOverlayPrefix ? overlayPrefix : "", variants, stexSource, shape, rcshape);
        }

        foreach ((string textureCode, CompositeTexture texture) in itemstack.Item.Textures)
        {
            stexSource.textures[textureCode] = texture;
        }

        VariantTextureMatcher.BakeVariantTextures(_clientApi, stexSource, variants, TexturesByType, prefixedTextureCodes, AddOverlayPrefix ? overlayPrefix : "");

        _clientApi?.Tesselator.TesselateShape("ShapeTexturesFromAttributes behavior", shape, out mesh, stexSource, quantityElements: rcshape.QuantityElements, selectiveElements: rcshape.SelectiveElements);
        return mesh;
    }

    public virtual MeshData GenMesh(ItemSlot itemSlot, ITextureAtlasAPI targetAtlas, BlockPos atBlockPos)
    {
        return itemSlot.Itemstack == null ? RenderExtensions.GenEmptyMesh() : GetOrCreateMesh(itemSlot.Itemstack, targetAtlas);
    }

    public virtual string GetMeshCacheKey(ItemSlot itemSlot)
    {
        return itemSlot.Itemstack == null ? "empty" : GetMeshCacheKey(itemSlot.Itemstack);
    }

    public virtual string GetMeshCacheKey(ItemStack itemstack)
    {
        string key = $"{itemstack.Collectible.Code}-{Variants.FromStack(itemstack)}";
        if (!RenderStoredStackOverlay) return key;

        ItemStack? storedStack = GetStoredStack(itemstack);
        if (storedStack?.Collectible?.Code == null) return $"{key}-stored-empty";

        return $"{key}-stored-{storedStack.Collectible.Code}-{Variants.FromStack(storedStack)}";
    }



    void IAttachableToEntity.CollectTextures(ItemStack stack, Shape shape, string texturePrefixCode, Dictionary<string, CompositeTexture> intoDict)
    {
        if (_api == null) return;

        Variants variants = Variants.FromStack(stack);

        if (shape.Textures != null)
        {
            foreach ((string textureCode, AssetLocation textureLocation) in shape.Textures.ToArray())
            {
                CompositeTexture ctex = VariantTextureMatcher.BakeTexture(_api, variants, new CompositeTexture(textureLocation));
                AddAttachableTexture(shape, intoDict, texturePrefixCode, textureCode, ctex);
            }
        }

        foreach ((string textureCode, CompositeTexture texture) in stack.Item.Textures)
        {
            CompositeTexture ctex = VariantTextureMatcher.BakeTexture(_api, variants, texture);
            AddAttachableTexture(shape, intoDict, texturePrefixCode, textureCode, ctex);
        }

        Dictionary<string, Dictionary<string, CompositeTexture>> texturesByType = new();

        if (stack.Collectible.GetCollectibleInterface<IShapeTexturesFromAttributes>() is IShapeTexturesFromAttributes fromAttributes)
        {
            texturesByType = fromAttributes.texturesByType;
        }

        foreach ((string textureCode, CompositeTexture texture) in VariantTextureMatcher.GetMatchingTextures(variants, texturesByType))
        {
            CompositeTexture ctex = VariantTextureMatcher.BakeTexture(_api, variants, texture);
            AddAttachableTexture(shape, intoDict, texturePrefixCode, textureCode, ctex);
        }

        if (RenderStoredStackOverlay)
        {
            InjectStoredStackOverlay(stack, shape, texturePrefixCode, intoDict);
        }
    }

    private static void AddAttachableTexture(Shape shape, Dictionary<string, CompositeTexture> intoDict, string texturePrefixCode, string textureCode, CompositeTexture ctex, string? targetTextureCode = null)
    {
        string shapeTextureCode = targetTextureCode ?? textureCode;
        string prefixedTextureCode = shapeTextureCode.StartsWith(texturePrefixCode, StringComparison.Ordinal)
            ? shapeTextureCode
            : texturePrefixCode + shapeTextureCode;

        intoDict[prefixedTextureCode] = ctex;

        shape.Textures ??= new Dictionary<string, AssetLocation>();
        if (ctex.Baked?.BakedName != null)
        {
            shape.Textures[prefixedTextureCode] = ctex.Baked.BakedName;
            shape.Textures[shapeTextureCode] = ctex.Baked.BakedName;
        }
    }

    CompositeShape IAttachableToEntity.GetAttachedShape(ItemStack stack, string slotCode)
    {
        Variants variants = Variants.FromStack(stack);
        variants.FindByVariant(ShapeByType, out CompositeShape ucshape);
        ucshape ??= stack.Item.Shape;
        CompositeShape rcshape = variants.ReplacePlaceholders(ucshape.Clone());

        return rcshape;
    }

    string IAttachableToEntity.GetCategoryCode(ItemStack stack)
    {
        if (CategoryCodeByType == null || !CategoryCodeByType.Any())
        {
            return _attachable?.GetCategoryCode(stack);
        }

        Variants variants = Variants.FromStack(stack);
        variants.FindByVariant(CategoryCodeByType, out string categoryCode);
        return categoryCode;
    }

    string[] IAttachableToEntity.GetDisableElements(ItemStack stack)
    {
        if (DisableElementsByType == null || !DisableElementsByType.Any())
        {
            return _attachable?.GetDisableElements(stack);
        }

        Variants variants = Variants.FromStack(stack);
        variants.FindByVariant(DisableElementsByType, out string[] disableElements);
        return disableElements;
    }

    string[] IAttachableToEntity.GetKeepElements(ItemStack stack)
    {
        if (KeepElementsByType == null || !KeepElementsByType.Any())
        {
            return _attachable?.GetKeepElements(stack);
        }

        Variants variants = Variants.FromStack(stack);
        variants.FindByVariant(KeepElementsByType, out string[] keepElements);
        return keepElements;
    }

    string IAttachableToEntity.GetTexturePrefixCode(ItemStack stack)
    {
        string texturePrefixCode = GetMeshCacheKey(stack);
        return texturePrefixCode;
    }

    bool IAttachableToEntity.IsAttachable(Entity toEntity, ItemStack itemStack)
    {
        return true;
    }

    int IAttachableToEntity.RequiresBehindSlots { get; set; }

    private void InjectStoredStackOverlay(ItemStack containerStack, Shape containerShape, string texturePrefixCode, Dictionary<string, CompositeTexture> intoDict)
    {
        if (_api == null) return;

        ItemStack? storedStack = GetStoredStack(containerStack);
        if (storedStack?.Collectible == null) return;

        CompositeShape? storedCompositeShape = GetStoredStackCompositeShape(storedStack);
        if (storedCompositeShape?.Base == null) return;

        AssetLocation storedShapeLocation = storedCompositeShape.Base.CopyWithPathPrefixAndAppendixOnce("shapes/", ".json");
        Shape? storedShape = _api.Assets.TryGet(storedShapeLocation)?.ToObject<Shape>();
        if (storedShape?.Elements == null || storedShape.Elements.Length == 0) return;

        PrefixStoredShapeTextureCodes(storedShape, StoredOverlayTexturePrefix);
        AddStoredStackTextures(storedStack, storedShape, containerShape, texturePrefixCode, intoDict);

        ShapeElement root = StoredStackOverlay.CreateRootElement();
        root.Children = storedShape.Elements;
        SetParentRecursive(root);

        containerShape.Elements = containerShape.Elements == null
            ? [root]
            : containerShape.Elements.Concat([root]).ToArray();
    }

    private ItemStack? GetStoredStack(ItemStack containerStack)
    {
        if (_api?.World == null) return null;

        ITreeAttribute? backpackTree = containerStack.Attributes.GetTreeAttribute("backpack");
        ITreeAttribute? slotsTree = backpackTree?.GetTreeAttribute("slots");
        if (slotsTree == null) return null;

        string preferredSlotKey = $"slot-{StoredStackOverlay.SlotIndex}";
        IAttribute? preferredAttribute = slotsTree[preferredSlotKey];
        ItemStack? preferredStack = preferredAttribute?.GetValue() as ItemStack;
        if (ResolveStoredStack(preferredStack)) return preferredStack;

        foreach ((_, IAttribute attribute) in slotsTree.SortedCopy())
        {
            ItemStack? storedStack = attribute?.GetValue() as ItemStack;
            if (ResolveStoredStack(storedStack)) return storedStack;
        }

        return null;
    }

    private bool ResolveStoredStack(ItemStack? storedStack)
    {
        if (storedStack == null || storedStack.StackSize <= 0 || _api?.World == null) return false;

        storedStack.ResolveBlockOrItem(_api.World);
        return storedStack.Collectible != null;
    }

    private CompositeShape? GetStoredStackCompositeShape(ItemStack storedStack)
    {
        Variants variants = Variants.FromStack(storedStack);
        CompositeShape? shape = null;

        if (storedStack.Collectible.GetCollectibleInterface<IShapeTexturesFromAttributes>() is IShapeTexturesFromAttributes fromAttributes)
        {
            variants.FindByVariant(fromAttributes.shapeByType, out shape);
        }

        if (shape == null)
        {
            Dictionary<string, CompositeShape>? shapeByType = storedStack.Collectible.Attributes?["shapeByType"].AsObject<Dictionary<string, CompositeShape>>();
            if (shapeByType != null)
            {
                variants.FindByVariant(shapeByType, out shape);
            }
        }

        shape ??= IAttachableToEntity.FromCollectible(storedStack.Collectible)?.GetAttachedShape(storedStack, StoredStackOverlay.SourceSlotCode);
        shape ??= storedStack.Class == EnumItemClass.Item
            ? storedStack.Item?.Shape
            : storedStack.Block?.Shape;

        return shape == null ? null : variants.ReplacePlaceholders(shape.Clone());
    }

    private void AddStoredStackTextures(ItemStack storedStack, Shape storedShape, Shape containerShape, string texturePrefixCode, Dictionary<string, CompositeTexture> intoDict)
    {
        if (_api == null) return;

        Variants storedVariants = Variants.FromStack(storedStack);
        if (storedShape.Textures != null)
        {
            foreach ((string textureCode, AssetLocation textureLocation) in storedShape.Textures.ToArray())
            {
                CompositeTexture ctex = VariantTextureMatcher.BakeTexture(_api, storedVariants, new CompositeTexture(textureLocation));
                AddAttachableTexture(containerShape, intoDict, texturePrefixCode, textureCode, ctex, ToStoredTextureCode(textureCode));
            }
        }

        IDictionary<string, CompositeTexture>? stackTextures = storedStack.Class == EnumItemClass.Item
            ? storedStack.Item?.Textures
            : storedStack.Block?.Textures;

        if (stackTextures != null)
        {
            foreach ((string textureCode, CompositeTexture texture) in stackTextures)
            {
                CompositeTexture ctex = VariantTextureMatcher.BakeTexture(_api, storedVariants, texture);
                AddAttachableTexture(containerShape, intoDict, texturePrefixCode, textureCode, ctex, ToStoredTextureCode(textureCode));
            }
        }

        Dictionary<string, Dictionary<string, CompositeTexture>> texturesByType = new();
        if (storedStack.Collectible.GetCollectibleInterface<IShapeTexturesFromAttributes>() is IShapeTexturesFromAttributes fromAttributes)
        {
            texturesByType = fromAttributes.texturesByType;
        }
        else
        {
            Dictionary<string, Dictionary<string, CompositeTexture>>? attributeTexturesByType = storedStack.Collectible.Attributes?["textures"].AsObject<Dictionary<string, Dictionary<string, CompositeTexture>>>();
            if (attributeTexturesByType != null)
            {
                texturesByType = attributeTexturesByType;
            }
        }

        foreach ((string textureCode, CompositeTexture texture) in VariantTextureMatcher.GetMatchingTextures(storedVariants, texturesByType))
        {
            CompositeTexture ctex = VariantTextureMatcher.BakeTexture(_api, storedVariants, texture);
            AddAttachableTexture(containerShape, intoDict, texturePrefixCode, textureCode, ctex, ToStoredTextureCode(textureCode));
        }
    }

    private static string ToStoredTextureCode(string textureCode)
    {
        return textureCode.StartsWith(StoredOverlayTexturePrefix, StringComparison.Ordinal)
            ? textureCode
            : StoredOverlayTexturePrefix + textureCode;
    }

    private static void PrefixStoredShapeTextureCodes(Shape shape, string prefix)
    {
        if (shape.Textures != null)
        {
            shape.Textures = shape.Textures.ToDictionary(entry => prefix + entry.Key, entry => entry.Value);
        }

        if (shape.TextureSizes != null)
        {
            shape.TextureSizes = shape.TextureSizes.ToDictionary(entry => prefix + entry.Key, entry => entry.Value);
        }

        if (shape.Elements == null) return;

        foreach (ShapeElement element in shape.Elements)
        {
            PrefixStoredElement(element, prefix);
        }
    }

    private static void PrefixStoredElement(ShapeElement element, string prefix)
    {
        element.Name = prefix + element.Name;
        element.StepParentName = null;

        if (element.FacesResolved != null)
        {
            foreach (ShapeElementFace? face in element.FacesResolved)
            {
                if (face?.Texture == null) continue;

                bool hasHash = face.Texture.StartsWith("#", StringComparison.Ordinal);
                string textureCode = hasHash ? face.Texture[1..] : face.Texture;
                face.Texture = hasHash ? "#" + prefix + textureCode : prefix + textureCode;
            }
        }

#pragma warning disable CS0618
        if (element.Faces != null)
        {
            foreach (ShapeElementFace face in element.Faces.Values)
            {
                if (face.Texture == null) continue;

                bool hasHash = face.Texture.StartsWith("#", StringComparison.Ordinal);
                string textureCode = hasHash ? face.Texture[1..] : face.Texture;
                face.Texture = hasHash ? "#" + prefix + textureCode : prefix + textureCode;
            }
        }
#pragma warning restore CS0618

        if (element.Children == null) return;

        foreach (ShapeElement child in element.Children)
        {
            PrefixStoredElement(child, prefix);
        }
    }

    private static void SetParentRecursive(ShapeElement element)
    {
        if (element.Children == null) return;

        foreach (ShapeElement child in element.Children)
        {
            child.ParentElement = element;
            SetParentRecursive(child);
        }
    }
}

public class StoredStackOverlayConfig
{
    public int SlotIndex { get; set; } = 0;
    public string Name { get; set; } = "BackStoredWeapon";
    public string StepParentName { get; set; } = "UpperTorso";
    public string SourceSlotCode { get; set; } = "backgear";
    public double[] From { get; set; } = [0.8, 4.2, -0.6];
    public double[] To { get; set; } = [0.8, 4.2, -0.6];
    public double[] RotationOrigin { get; set; } = [0.8, 4.2, -0.6];
    public double RotationX { get; set; } = 0;
    public double RotationY { get; set; } = -86;
    public double RotationZ { get; set; } = -55;
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public double ScaleZ { get; set; } = 1;

    public ShapeElement CreateRootElement()
    {
        return new ShapeElement
        {
            Name = Name,
            StepParentName = StepParentName,
            From = From,
            To = To,
            RotationOrigin = RotationOrigin,
            RotationX = RotationX,
            RotationY = RotationY,
            RotationZ = RotationZ,
            ScaleX = ScaleX,
            ScaleY = ScaleY,
            ScaleZ = ScaleZ,
            FacesResolved = null
        };
    }
}
