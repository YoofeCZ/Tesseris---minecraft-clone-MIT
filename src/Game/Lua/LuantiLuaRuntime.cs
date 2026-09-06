using MoonSharp.Interpreter;
using OpenTK.Mathematics;
using Tesseris.Engine.Core;
using Tesseris.Game.Entities;

namespace Tesseris.Game.Lua;

/// <summary>
/// Small, deliberately compatible slice of Luanti's builtin entity API. C# owns rendering,
/// collision and persistence; Lua owns registered prototypes and lifecycle behaviour.
/// </summary>
public sealed class LuantiLuaRuntime : IDisposable
{
    private readonly Script script;
    private readonly Table core;
    private readonly Dictionary<string, LuaEntityDefinition> definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<long, LuaEntityInstance> instances = [];
    private string currentModName = "builtin";
    private string currentModPath = string.Empty;
    private bool disposed;

    public LuantiLuaRuntime()
    {
        script = new Script(CoreModules.Preset_SoftSandbox);
        script.Options.DebugPrint = message => Log.Info($"Lua: {message}");
        core = new Table(script);
        core["registered_entities"] = new Table(script);
        core["register_entity"] = DynValue.NewCallback(RegisterEntity);
        core["get_current_modname"] = DynValue.NewCallback((_, _) => DynValue.NewString(currentModName));
        core["get_modpath"] = DynValue.NewCallback(GetModPath);
        core["log"] = DynValue.NewCallback(LogFromLua);
        script.Globals["core"] = core;
        script.Globals["minetest"] = core;
    }

    public IReadOnlyCollection<string> RegisteredEntityNames => definitions.Keys;

    public static LuantiLuaRuntime Load(string builtinRoot, string modsRoot)
    {
        var runtime = new LuantiLuaRuntime();
        string builtin = Path.Combine(builtinRoot, "lua", "tesseris_mobs", "init.lua");
        if (File.Exists(builtin))
            runtime.ExecuteFile(builtin, "builtin");

        if (Directory.Exists(modsRoot))
        {
            foreach (string directory in Directory.EnumerateDirectories(modsRoot)
                         .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string init = Path.Combine(directory, "init.lua");
                if (File.Exists(init))
                    runtime.ExecuteFile(init, Path.GetFileName(directory));
            }
        }

        return runtime;
    }

    public void ExecuteFile(string path, string modName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Execute(File.ReadAllText(fullPath), modName, Path.GetDirectoryName(fullPath)!, fullPath);
    }

    public void ExecuteString(string source, string modName = "test") =>
        Execute(source, modName, Directory.GetCurrentDirectory(), $"={modName}/init.lua");

    private void Execute(string source, string modName, string modPath, string sourceName)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        string previousName = currentModName;
        string previousPath = currentModPath;
        currentModName = string.IsNullOrWhiteSpace(modName) ? "unknown" : modName;
        currentModPath = modPath;
        try
        {
            script.DoString(source, null, sourceName);
            Log.Info($"Lua mod '{currentModName}' loaded.");
        }
        catch (InterpreterException exception)
        {
            Log.Warn($"Lua mod '{currentModName}' failed: {exception.DecoratedMessage ?? exception.Message}");
        }
        finally
        {
            currentModName = previousName;
            currentModPath = previousPath;
        }
    }

    public bool Attach(AnimalEntity animal, string staticData = "")
    {
        ArgumentNullException.ThrowIfNull(animal);
        Detach(animal, removal: false);
        string definitionId = string.IsNullOrWhiteSpace(animal.DefinitionId)
            ? MobDefinitions.For(animal.Kind).Id
            : animal.DefinitionId;
        animal.DefinitionId = definitionId;
        if (!definitions.TryGetValue(definitionId, out LuaEntityDefinition? definition))
            return false;

        var entity = new Table(script) { MetaTable = new Table(script) };
        entity.MetaTable["__index"] = definition.Prototype;
        var instance = new LuaEntityInstance(animal, definition, entity);
        entity["object"] = CreateObjectRef(instance);
        instances[animal.Id] = instance;
        Invoke(instance, "on_activate", DynValue.NewString(staticData ?? string.Empty), DynValue.NewNumber(0));
        return true;
    }

    public void Step(AnimalEntity animal, float deltaSeconds)
    {
        if (!instances.TryGetValue(animal.Id, out LuaEntityInstance? instance))
            return;
        Invoke(instance, "on_step", DynValue.NewNumber(deltaSeconds), EmptyMoveresult());
    }

    public void Punch(AnimalEntity animal, float damage, Vector3 direction)
    {
        if (!instances.TryGetValue(animal.Id, out LuaEntityInstance? instance))
            return;
        Invoke(instance, "on_punch", DynValue.Nil, DynValue.NewNumber(0), DynValue.Nil,
            Vector(direction), DynValue.NewNumber(damage));
    }

    public void Death(AnimalEntity animal)
    {
        if (!instances.TryGetValue(animal.Id, out LuaEntityInstance? instance))
            return;
        Invoke(instance, "on_death", DynValue.Nil);
        Detach(animal, removal: true);
    }

    public string GetStaticData(AnimalEntity animal)
    {
        if (!instances.TryGetValue(animal.Id, out LuaEntityInstance? instance))
            return animal.LuaStaticData ?? string.Empty;
        DynValue result = Invoke(instance, "get_staticdata");
        string value = result.Type == DataType.String ? result.String : string.Empty;
        animal.LuaStaticData = value;
        return value;
    }

    public void Detach(AnimalEntity animal, bool removal)
    {
        if (!instances.Remove(animal.Id, out LuaEntityInstance? instance))
            return;
        Invoke(instance, "on_deactivate", DynValue.NewBoolean(removal));
    }

    private DynValue RegisterEntity(ScriptExecutionContext _, CallbackArguments args)
    {
        string name = args.AsType(0, "register_entity", DataType.String, false).String;
        Table prototype = args.AsType(1, "register_entity", DataType.Table, false).Table;
        if (string.IsNullOrWhiteSpace(name) || !name.Contains(':', StringComparison.Ordinal))
            throw new ScriptRuntimeException("register_entity: name must be namespaced");
        prototype["name"] = name;
        prototype["mod_origin"] = currentModName;
        definitions[name] = new LuaEntityDefinition(name, prototype, currentModName);
        core.Get("registered_entities").Table[name] = prototype;
        return DynValue.Nil;
    }

    private DynValue GetModPath(ScriptExecutionContext _, CallbackArguments args)
    {
        string requested = args.Count > 0 && args[0].Type == DataType.String ? args[0].String : currentModName;
        return requested == currentModName ? DynValue.NewString(currentModPath) : DynValue.Nil;
    }

    private static DynValue LogFromLua(ScriptExecutionContext _, CallbackArguments args)
    {
        string level = args.Count > 1 ? args[0].CastToString() : "action";
        string message = args.Count > 1 ? args[1].CastToString() : args[0].CastToString();
        if (level is "error") Log.Error($"Lua: {message}");
        else if (level is "warning" or "deprecated") Log.Warn($"Lua: {message}");
        else Log.Info($"Lua: {message}");
        return DynValue.Nil;
    }

    private Table CreateObjectRef(LuaEntityInstance instance)
    {
        var value = new Table(script);
        value["get_pos"] = Method(_ => Vector(instance.Animal.Position));
        value["set_pos"] = Method(args => { instance.Animal.Position = ReadVector(args, 1); return DynValue.Nil; });
        value["get_velocity"] = Method(_ => Vector(instance.Animal.ScriptVelocity));
        value["set_velocity"] = Method(args =>
        {
            instance.Animal.ScriptVelocity = ReadVector(args, 1);
            instance.Animal.LuaControlsMovement = true;
            return DynValue.Nil;
        });
        value["get_acceleration"] = Method(_ => Vector(instance.Animal.ScriptAcceleration));
        value["set_acceleration"] = Method(args =>
        {
            instance.Animal.ScriptAcceleration = ReadVector(args, 1);
            instance.Animal.LuaControlsMovement = true;
            return DynValue.Nil;
        });
        value["get_yaw"] = Method(_ => DynValue.NewNumber(instance.Animal.Yaw));
        value["set_yaw"] = Method(args => { instance.Animal.Yaw = (float)args.AsType(1, "set_yaw", DataType.Number, false).Number; return DynValue.Nil; });
        value["get_hp"] = Method(_ => DynValue.NewNumber(instance.Animal.Health));
        value["set_hp"] = Method(args => { instance.Animal.Health = Math.Max(0f, (float)args.AsType(1, "set_hp", DataType.Number, false).Number); return DynValue.Nil; });
        value["is_player"] = Method(_ => DynValue.False);
        value["get_luaentity"] = Method(_ => DynValue.NewTable(instance.Entity));
        value["remove"] = Method(_ => { instance.Animal.RemovalRequested = true; return DynValue.Nil; });
        value["get_properties"] = Method(_ => GetProperties(instance));
        value["set_properties"] = Method(args => { SetProperties(instance, args.AsType(1, "set_properties", DataType.Table, false).Table); return DynValue.Nil; });
        value["set_animation"] = Method(_ => DynValue.Nil);
        return value;
    }

    private DynValue GetProperties(LuaEntityInstance instance)
    {
        DynValue source = instance.Definition.Prototype.Get("initial_properties");
        return source.Type == DataType.Table ? source : DynValue.NewTable(new Table(script));
    }

    private static void SetProperties(LuaEntityInstance instance, Table properties)
    {
        DynValue hpMax = properties.Get("hp_max");
        if (hpMax.Type == DataType.Number && hpMax.Number > 0 && instance.Animal.Health > hpMax.Number)
            instance.Animal.Health = (float)hpMax.Number;
    }

    private DynValue EmptyMoveresult()
    {
        var result = new Table(script);
        result["touching_ground"] = true;
        result["collides"] = false;
        return DynValue.NewTable(result);
    }

    private DynValue Invoke(LuaEntityInstance instance, string callback, params DynValue[] args)
    {
        DynValue function = instance.Definition.Prototype.Get(callback);
        if (function.Type is not (DataType.Function or DataType.ClrFunction))
            return DynValue.Nil;
        try
        {
            var callArgs = new DynValue[args.Length + 1];
            callArgs[0] = DynValue.NewTable(instance.Entity);
            args.CopyTo(callArgs, 1);
            return script.Call(function, callArgs);
        }
        catch (InterpreterException exception)
        {
            Log.Warn($"Lua entity '{instance.Definition.Name}' {callback} failed: {exception.DecoratedMessage ?? exception.Message}");
            return DynValue.Nil;
        }
    }

    private DynValue Vector(Vector3 vector)
    {
        var table = new Table(script);
        table["x"] = vector.X;
        table["y"] = vector.Y;
        table["z"] = vector.Z;
        return DynValue.NewTable(table);
    }

    private static Vector3 ReadVector(CallbackArguments args, int index)
    {
        Table value = args.AsType(index, "ObjectRef vector", DataType.Table, false).Table;
        return new Vector3(ReadFinite(value, "x"), ReadFinite(value, "y"), ReadFinite(value, "z"));
    }

    private static float ReadFinite(Table table, string key)
    {
        DynValue value = table.Get(key);
        if (value.Type != DataType.Number || !double.IsFinite(value.Number))
            throw new ScriptRuntimeException($"vector.{key} must be a finite number");
        return (float)value.Number;
    }

    private static DynValue Method(Func<CallbackArguments, DynValue> callback) =>
        DynValue.NewCallback((_, args) => callback(args));

    public void Dispose()
    {
        if (disposed) return;
        foreach (LuaEntityInstance instance in instances.Values.ToArray())
            Invoke(instance, "on_deactivate", DynValue.False);
        instances.Clear();
        disposed = true;
    }

    private sealed record LuaEntityDefinition(string Name, Table Prototype, string ModOrigin);
    private sealed record LuaEntityInstance(AnimalEntity Animal, LuaEntityDefinition Definition, Table Entity);
}
