using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Noggog;
using Activator = System.Activator;

namespace SehtMcp;

/// <summary>A bounded, allowlisted JSON surface over Mutagen's strongly typed records.</summary>
public static class RecordCodec
{
    private static readonly Type[] Types = typeof(SkyrimMod).Assembly.GetTypes()
        .Where(t => t.IsPublic && !t.ContainsGenericParameters && t.Namespace == "Mutagen.Bethesda.Skyrim").ToArray();
    private static readonly HashSet<string> Hidden = ["StaticRegistration", "TitleString", "FormKey", "MajorRecordFlagsRaw", "SkyrimMajorRecordFlags", "VersionControl", "Version2", "FormVersion"];
    private static readonly JsonSerializerOptions PrimitiveJson = new() { IncludeFields = true, PropertyNameCaseInsensitive = true };

    public static Type ResolveType(string name) => Types.SingleOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown Skyrim type '{name}'. Use record_types or record_schema.");
    public static Type[] Implementations(Type t) => Types.Where(x => !x.IsAbstract && !x.IsInterface && t.IsAssignableFrom(x)).OrderBy(x => x.Name).ToArray();
    public static PropertyInfo[] Properties(Type t) => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod?.IsPublic == true && !Hidden.Contains(p.Name)).ToArray();
    private static Type? ElementType(Type t) => t.GetInterfaces().Append(t).FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
    private static bool NestedRecords(Type t) => typeof(IMajorRecordGetter).IsAssignableFrom(t) || (ElementType(t) is { } e && typeof(IMajorRecordGetter).IsAssignableFrom(e));
    private static bool LinkOrIndex(Type t) => t.GetInterfaces().Append(t).Any(i => i.Name.StartsWith("IFormLinkOrIndexGetter`"));
    public static bool Editable(PropertyInfo p) => (p.SetMethod?.IsPublic == true || LinkOrIndex(p.PropertyType)) && !NestedRecords(p.PropertyType);

    public static object Schema(string name, int depth = 1)
    {
        if (depth is < 0 or > 3) throw new ArgumentException("Schema depth must be 0..3.");
        return Describe(ResolveType(name), depth, []);
    }
    private static object Describe(Type original, int depth, HashSet<Type> seen)
    {
        var t = Nullable.GetUnderlyingType(original) ?? original;
        var element = t == typeof(string) ? null : ElementType(t);
        return new
        {
            type = t.Name, nullable = !original.IsValueType || Nullable.GetUnderlyingType(original) is not null,
            enumValues = t.IsEnum ? Enum.GetNames(t) : null, flags = t.IsDefined(typeof(FlagsAttribute)),
            representation = LinkOrIndex(t) ? "FormKey string or {link: FormKey} or {index: integer}; owning condition flags control index interpretation" : typeof(IFormLinkGetter).IsAssignableFrom(t) || t == typeof(FormKey) ? "000800:Plugin.esp (null clears link)" : typeof(ITranslatedStringGetter).IsAssignableFrom(t) ? "string or language map (English/default embedded on save)" : typeof(IAssetLinkGetter).IsAssignableFrom(t) ? "Data-relative asset path" : null,
            elementType = element?.Name,
            linkTarget = typeof(IFormLinkGetter).IsAssignableFrom(t) && t.IsGenericType ? t.GetGenericArguments()[0].Name : null,
            variants = t.IsInterface || t.IsAbstract ? Implementations(t).Select(x => x.Name).ToArray() : null,
            fields = depth > 0 && seen.Add(t) && !t.IsEnum && t != typeof(string) && element is null && !typeof(IFormLinkGetter).IsAssignableFrom(t)
                ? Properties(t).Select(p => new { name = p.Name, writable = Editable(p), schema = Describe(p.PropertyType, depth - 1, new(seen)) }).ToArray() : null
        };
    }

    public static JsonNode? Encode(object? value, int depth = 8, int maxItems = 250)
    {
        if (value is null) return null;
        if (value is IFormLinkGetter link) return link.IsNull ? null : JsonValue.Create(link.FormKey.ToString());
        if (value is FormKey key) return JsonValue.Create(key.ToString());
        if (value is IAssetLinkGetter asset) return JsonValue.Create(asset.DataRelativePath.ToString());
        if (value is ITranslatedStringGetter translated)
        {
            var result = new JsonObject();
            foreach (var pair in translated) result[pair.Key.ToString()] = pair.Value;
            return result;
        }
        var t = value.GetType();
        if (LinkOrIndex(t)) return new JsonObject { ["link"] = Encode(t.GetProperty("Link")!.GetValue(value)), ["index"] = Encode(t.GetProperty("Index")!.GetValue(value)) };
        if (t.IsEnum) return JsonValue.Create(value.ToString());
        if (t.IsPrimitive || t == typeof(string) || t == typeof(decimal)) return JsonSerializer.SerializeToNode(value, t, PrimitiveJson);
        if (depth <= 0) return new JsonObject { ["$truncated"] = true, ["$type"] = t.Name };
        if (value is IEnumerable collection)
        {
            var result = new JsonArray();
            var count = 0;
            foreach (var item in collection)
            {
                if (count++ >= maxItems) { result.Add(new JsonObject { ["$truncated"] = true }); break; }
                result.Add(Encode(item, depth - 1, maxItems));
            }
            return result;
        }
        var obj = new JsonObject { ["$type"] = t.Name };
        if (value is IMajorRecordGetter record) obj["FormKey"] = record.FormKey.ToString();
        foreach (var p in Properties(t).Where(p => !t.IsValueType || p.SetMethod?.IsPublic == true)) obj[p.Name] = Encode(p.GetValue(value), depth - 1, maxItems);
        return obj;
    }

    public static void Apply(object target, JsonObject fields, string path = "", int depth = 0)
    {
        if (depth > 24) throw new ArgumentException("Edit nesting exceeds 24 levels.");
        foreach (var (name, node) in fields)
        {
            if (name == "$type") continue;
            var p = Properties(target.GetType()).SingleOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p is null || !Editable(p)) throw new ArgumentException($"Field '{path}{name}' is unknown or read-only. Use record_schema. Nested major records need record_create with a parent.");
            try { var decoded = Decode(node, p.PropertyType, p.GetValue(target), path + name + ".", depth + 1); if (p.SetMethod?.IsPublic == true) p.SetValue(target, decoded); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { throw new ArgumentException($"Invalid {path}{name}: {ex.GetBaseException().Message}", ex); }
        }
    }

    private static object? Decode(JsonNode? node, Type declared, object? existing, string path, int depth)
    {
        if (depth > 24) throw new ArgumentException("Edit nesting exceeds 24 levels.");
        var t = Nullable.GetUnderlyingType(declared) ?? declared;
        if (LinkOrIndex(t))
        {
            var holder = existing ?? throw new ArgumentException("Link-or-index requires its owning condition.");
            var holderType = holder.GetType();
            var link = holderType.GetProperty("Link")!.GetValue(holder)!;
            var indexValue = node is JsonObject oi ? oi["index"] : null;
            var linkValue = node is JsonObject ol ? ol["link"] : node;
            if (indexValue is not null && linkValue is not null) throw new ArgumentException("Supply link or index, not both.");
            link.GetType().GetProperty("FormKey")!.SetValue(link, linkValue is null ? null : FormKey.Factory(linkValue.GetValue<string>()));
            holderType.GetProperty("Index")!.SetValue(holder, indexValue?.GetValue<uint>());
            return holder;
        }
        if (typeof(IFormLinkGetter).IsAssignableFrom(t))
        {
            var generic = t.GetInterfaces().Append(t).First(x => x.IsGenericType && x.Name.StartsWith("IFormLinkGetter`"));
            var nullable = t.GetInterfaces().Append(t).Any(x => x.Name.StartsWith("IFormLinkNullable"));
            var concrete = (nullable ? typeof(FormLinkNullable<>) : typeof(FormLink<>)).MakeGenericType(generic.GetGenericArguments());
            return node is null ? Activator.CreateInstance(concrete) : Activator.CreateInstance(concrete, FormKey.Factory(node.GetValue<string>()));
        }
        if (node is null)
        {
            if (declared.IsValueType && Nullable.GetUnderlyingType(declared) is null) throw new ArgumentException("Cannot clear a required value.");
            return null;
        }
        if (typeof(ITranslatedStringGetter).IsAssignableFrom(t))
        {
            var translated = new TranslatedString(Language.English);
            if (node is JsonValue) translated.String = node.GetValue<string>();
            else foreach (var (lang, text) in node.AsObject()) translated.Set(Enum.Parse<Language>(lang, true), text?.GetValue<string>() ?? "");
            return translated;
        }
        if (typeof(IAssetLinkGetter).IsAssignableFrom(t))
        {
            var raw = node.GetValue<string>();
            AssetService.Normalize(raw);
            var concrete = t.IsInterface ? typeof(AssetLink<>).MakeGenericType(t.GetGenericArguments()) : t;
            return Activator.CreateInstance(concrete, raw);
        }
        if (t == typeof(FormKey)) return FormKey.Factory(node.GetValue<string>());
        if (t.IsEnum)
        {
            if (node.ToJsonString() == "0" || node.ToJsonString() == "\"0\"") return Enum.ToObject(t, 0);
            var text = node.GetValue<string>();
            foreach (var part in text.Split(',')) if (!Enum.GetNames(t).Contains(part.Trim(), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"Unknown {t.Name} value '{part}'.");
            return Enum.Parse(t, text, true);
        }
        if (t.IsPrimitive || t == typeof(string) || t == typeof(decimal)) return node.Deserialize(t, PrimitiveJson);
        if (node is JsonArray array && ElementType(t) is { } element)
        {
            if (array.Count > 10000) throw new ArgumentException("Collection exceeds 10000 items.");
            if (t == typeof(MemorySlice<byte>)) return new MemorySlice<byte>(array.Select(n => n!.GetValue<byte>()).ToArray());
            if (t.IsArray) { var result = Array.CreateInstance(element, array.Count); for (var i = 0; i < array.Count; i++) result.SetValue(Decode(array[i], element, null, path, depth + 1), i); return result; }
            var list = Activator.CreateInstance(t) ?? throw new ArgumentException($"Cannot create {t.Name}.");
            var add = t.GetMethod("Add", [element]) ?? throw new ArgumentException($"Unsupported collection {t.Name}.");
            foreach (var item in array) add.Invoke(list, [Decode(item, element, null, path, depth + 1)]);
            return list;
        }
        if (node is not JsonObject obj) throw new ArgumentException($"Expected object for {t.Name}.");
        if (obj["$type"] is { } typeNode)
        {
            var requested = ResolveType(typeNode.GetValue<string>());
            if (!t.IsAssignableFrom(requested) || requested.IsAbstract || requested.IsInterface || typeof(IMajorRecordGetter).IsAssignableFrom(requested)) throw new ArgumentException("Invalid $type for this field.");
            t = requested;
        }
        else if (existing is not null) t = existing.GetType();
        else if (t.IsAbstract || t.IsInterface)
        {
            var variants = Implementations(t);
            if (variants.Length != 1) throw new ArgumentException($"Specify $type: {string.Join(", ", variants.Select(x => x.Name))}");
            t = variants[0];
        }
        var value = existing?.GetType() == t ? existing : Activator.CreateInstance(t) ?? throw new ArgumentException($"Cannot create {t.Name}.");
        Apply(value, obj, path, depth);
        return value;
    }
}
