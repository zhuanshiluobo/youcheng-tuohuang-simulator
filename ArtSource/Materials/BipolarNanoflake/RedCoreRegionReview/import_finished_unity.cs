// Unity MCP execute_code 的方法体。仅更新双极纳米片模型及展示预制体，不保存场景。
if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
    throw new System.InvalidOperationException("请先停止 Play。");
if (Application.dataPath.Replace('\\', '/') != "F:/游城拓荒模拟器/Assets")
    throw new System.InvalidOperationException("Unity 项目不匹配。");
var source = "F:/游城拓荒模拟器/ArtSource/Materials/BipolarNanoflake/RedCoreRegionReview/";
var folder = "Assets/YC/Presentation/Models/BipolarNanoflake/";
var fbxPath = folder + "BipolarNanoflake_Finished.fbx";
var prefabPath = "Assets/YC/Presentation/Collection/Prefabs/BipolarNanoflakeDisplay.prefab";
var manifest = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(source + "finished_material_manifest.json"));
var materialData = (Newtonsoft.Json.Linq.JObject)manifest["materials"];
var textureNames = (Newtonsoft.Json.Linq.JObject)manifest["textures"];
var basePath = folder + "Textures/" + System.IO.Path.GetFileName((string)textureNames["BaseColor"]);
var emissionPath = folder + "Textures/" + System.IO.Path.GetFileName((string)textureNames["Emission"]);
foreach (var path in new[] { fbxPath, basePath, emissionPath })
    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
foreach (var path in new[] { basePath, emissionPath })
{
    var importer = (TextureImporter)AssetImporter.GetAtPath(path);
    importer.textureType = TextureImporterType.Default;
    importer.sRGBTexture = true;
    importer.alphaSource = TextureImporterAlphaSource.None;
    importer.maxTextureSize = 1024;
    importer.textureCompression = TextureImporterCompression.Uncompressed;
    importer.mipmapEnabled = true;
    importer.wrapMode = TextureWrapMode.Clamp;
    importer.SaveAndReimport();
}
var baseTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(basePath);
var emissionTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(emissionPath);
if (baseTexture == null || emissionTexture == null)
    throw new System.InvalidOperationException("核心贴图未成功导入。");
var mapped = new System.Collections.Generic.Dictionary<string, Material>(System.StringComparer.Ordinal);
foreach (var entry in materialData.Properties())
{
    var settings = (Newtonsoft.Json.Linq.JObject)entry.Value;
    var core = (string)settings["shaderType"] == "Standard";
    var shader = Shader.Find(core ? "Standard" : "YC/Collection/ExhibitPBR");
    if (shader == null) throw new System.InvalidOperationException("缺少模型使用的 shader。");
    var matPath = folder + "Materials/" + entry.Name + ".mat";
    var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
    if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, matPath); }
    mat.name = entry.Name;
    mat.shader = shader;
    mat.SetFloat("_Metallic", (float)settings["metallic"]);
    mat.SetFloat("_Glossiness", (float)settings["smoothness"]);
    if (core)
    {
        var albedoScale = settings["unityAlbedoMultiplierLinear"] == null ? 1f : (float)settings["unityAlbedoMultiplierLinear"];
        mat.SetColor("_Color", new Color(albedoScale, albedoScale, albedoScale, 1).gamma);
        mat.SetTexture("_MainTex", baseTexture);
        mat.SetTexture("_EmissionMap", emissionTexture);
        var scale = (float)(settings["emissionTextureScale"] ?? manifest["emissionTextureScale"]);
        if (settings["unityEmissionScale"] != null) scale = (float)settings["unityEmissionScale"];
        // manifest 为线性值；Color 类型的 shader 属性由 Unity 转成线性空间。
        mat.SetColor("_EmissionColor", new Color(scale, scale, scale, 1).gamma);
        var highlights = settings["unitySpecularHighlights"] == null || (bool)settings["unitySpecularHighlights"];
        mat.SetFloat("_SpecularHighlights", highlights ? 1 : 0);
        if (highlights) mat.DisableKeyword("_SPECULARHIGHLIGHTS_OFF"); else mat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
        mat.SetFloat("_Mode", 0);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
        mat.SetInt("_ZWrite", 1);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.DisableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.SetOverrideTag("RenderType", "Opaque");
        mat.renderQueue = -1;
        mat.EnableKeyword("_EMISSION");
        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
    }
    else
    {
        var color = (Newtonsoft.Json.Linq.JArray)settings["color"];
        var c = new Color((float)color[0], (float)color[1], (float)color[2], 1).gamma;
        c.a = color.Count > 3 ? (float)color[3] : 1;
        mat.SetColor("_Color", c);
        var emission = (Newtonsoft.Json.Linq.JArray)settings["emission"];
        var e = emission == null ? Color.black : new Color((float)emission[0], (float)emission[1], (float)emission[2], 1);
        mat.SetColor("_EmissionColor", e.gamma);
        if (e.r > 0 || e.g > 0 || e.b > 0) mat.EnableKeyword("_EMISSION"); else mat.DisableKeyword("_EMISSION");
        mat.SetFloat("_LightResponse", 0.35f);
        mat.SetFloat("_EnvironmentResponse", 0.7f);
        mat.SetFloat("_HighlightCompression", 1);
    }
    EditorUtility.SetDirty(mat);
    AssetDatabase.SaveAssetIfDirty(mat);
    mapped.Add(entry.Name, mat);
}
var modelImporter = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
foreach (var entry in mapped)
    modelImporter.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), entry.Key), entry.Value);
modelImporter.SaveAndReimport();
var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
if (model == null) throw new System.InvalidOperationException("FBX 未成功导入。");
var prefab = PrefabUtility.LoadPrefabContents(prefabPath);
try
{
    var pose = prefab.transform.Find("展示姿态");
    if (pose == null || pose.childCount != 1) throw new System.InvalidOperationException("展示姿态结构不匹配。");
    var oldModel = pose.GetChild(0).gameObject;
    var replacement = (GameObject)UnityEngine.Object.Instantiate(model);
    replacement.name = oldModel.name;
    replacement.transform.SetParent(pose, false);
    foreach (var renderer in replacement.GetComponentsInChildren<MeshRenderer>(true))
    {
        var slots = renderer.sharedMaterials;
        for (int i = 0; i < slots.Length; i++)
        {
            Material value;
            if (slots[i] == null || !mapped.TryGetValue(slots[i].name, out value))
                throw new System.InvalidOperationException("材质槽未映射：" + renderer.name);
            slots[i] = value;
        }
        renderer.sharedMaterials = slots;
    }
    // 删除的只是加载预制体内容中的旧模型对象；不删除任何磁盘文件。
    UnityEngine.Object.DestroyImmediate(oldModel);
    PrefabUtility.SaveAsPrefabAsset(prefab, prefabPath);
}
finally { PrefabUtility.UnloadPrefabContents(prefab); }
return new { modelGuid = AssetDatabase.AssetPathToGUID(fbxPath), prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath), materials = mapped.Count };
