﻿using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Internal.Notifications;
using Lumina.Excel.GeneratedSheets;
using Newtonsoft.Json;

namespace Splatoon;
unsafe class Splatoon : IDalamudPlugin
{
    public string Name => "Splatoon";
    internal Gui DrawingGui;
    internal CGui ConfigGui;
    internal Commands CommandManager;
    internal IMemoryManager MemoryManager;
    internal ChlogGui ChangelogGui = null;
    internal Configuration Config;
    internal MemerrGui memerrGui;
    internal Dictionary<ushort, TerritoryType> Zones;
    internal string[] LogStorage = new string[100];
    internal long CombatStarted = 0;
    internal HashSet<DisplayObject> displayObjects = new();
    internal HashSet<Element> injectedElements = new();
    internal double CamAngleX;
    internal Dictionary<int, string> Jobs = new();
    //internal HashSet<(float x, float y, float z, float r, float angle)> draw = new HashSet<(float x, float y, float z, float r, float angle)>();
    internal float CamAngleY;
    internal float CamZoom = 1.5f;
    internal bool prevMouseState = false;
    internal SearchInfo SFind = null;
    internal int CurrentLineSegments;
    internal ConcurrentQueue<System.Action> tickScheduler;
    internal List<DynamicElement> dynamicElements;
    internal HTTPServer HttpServer;
    internal bool prevCombatState = false;
    static internal Vector3? PlayerPosCache = null;
    internal Profiling Profiler;
    internal Queue<string> ChatMessageQueue;
    internal HashSet<string> CurrentChatMessages = new();
    internal Element Clipboard = null;
    internal int dequeueConcurrency = 1;
    internal Dictionary<(string Name, uint ObjectID, uint DataID, int ModelID, ObjectKind type), ObjectInfo> loggedObjectList = new();
    internal bool LogObjects = false;
    internal bool DisableLineFix = false;
    internal int Phase = 1;
    internal int LayoutAmount = 0;
    internal int ElementAmount = 0;
    /*internal static readonly string[] LimitGaugeResets = new string[] 
    {
        "The limit gauge resets!",
        "リミットゲージがリセットされた……",
        "Der Limitrausch-Balken wurde geleert.",
        "La jauge de Transcendance a été réinitialisée.",
        "极限槽被清零了……"
    };*/
    internal static string LimitGaugeResets = "";
    public static bool Init = false;

    public void Dispose()
    {
        Init = false;
        Config.Save();
        SetupShutdownHttp(false);
        DrawingGui.Dispose();
        ConfigGui.Dispose();
        CommandManager.Dispose();
        Svc.ClientState.TerritoryChanged -= TerritoryChangedEvent;
        Svc.Framework.Update -= Tick;
        Svc.Chat.ChatMessage -= OnChatMessage;
        //Svc.Chat.Print("Disposing");
    }

    public Splatoon(DalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Svc>();
        //Svc.Chat.Print("Loaded");
        var configRaw = Svc.PluginInterface.GetPluginConfig();
        Config = configRaw as Configuration ?? new Configuration();
        Config.Initialize(this);
        if(configRaw == null)
        {
            Notify("New configuration file has been created");
            Config.Save();
        }
        ChatMessageQueue = new Queue<string>();
        Profiler = new Profiling(this);
        CommandManager = new Commands(this);
        Zones = Svc.Data.GetExcelSheet<TerritoryType>().ToDictionary(row => (ushort)row.RowId, row => row);
        Jobs = Svc.Data.GetExcelSheet<ClassJob>().ToDictionary(row => (int)row.RowId, row => row.Name.ToString());
        if(ChlogGui.ChlogVersion > Config.ChlogReadVer && ChangelogGui == null)
        {
            ChangelogGui = new ChlogGui(this);
            Config.NoMemory = false;
        }
        MemoryManager = new GlobalMemory(this);
        if (MemoryManager.ErrorCode != 0)
        {
            memerrGui = new MemerrGui(this);
        }
        tickScheduler = new ConcurrentQueue<System.Action>();
        dynamicElements = new List<DynamicElement>();
        SetupShutdownHttp(Config.UseHttpServer);

        DrawingGui = new Gui(this);
        ConfigGui = new CGui(this);
        Svc.Chat.ChatMessage += OnChatMessage;
        Svc.Framework.Update += Tick;
        Svc.ClientState.TerritoryChanged += TerritoryChangedEvent;
        Svc.PluginInterface.UiBuilder.DisableUserUiHide = Config.ShowOnUiHide;
        LimitGaugeResets = Svc.Data.GetExcelSheet<LogMessage>().GetRow(2844).Text.ToString();
        Init = true;
    }

    internal static readonly string[] InvalidSymbols = { "", "", "", "“", "”", "" };
    private void OnChatMessage(XivChatType type, uint senderId, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (Profiler.Enabled) Profiler.MainTickChat.StartTick();
        var inttype = (int)type;
        if(inttype == 2105 && LimitGaugeResets.Equals(message.ToString()))
        {
            Phase++;
            CombatStarted = Environment.TickCount64;
            Svc.PluginInterface.UiBuilder.AddNotification($"Phase transition to Phase {Phase}", this.Name, NotificationType.Info, 10000);
        }
        if(!Config.LimitTriggerMessages || inttype == 68 || inttype == 2105 || type == XivChatType.SystemMessage)
        {
            ChatMessageQueue.Enqueue(message.Payloads.Where(p => p is ITextProvider)
                    .Cast<ITextProvider>()
                    .Aggregate(new StringBuilder(), (sb, tp) => sb.Append(tp.Text.RemoveSymbols(InvalidSymbols)), sb => sb.ToString()));
        }
        if (Profiler.Enabled) Profiler.MainTickChat.StopTick();
    }

    internal void SetupShutdownHttp(bool enable)
    {
        if (enable)
        {
            if(HttpServer == null)
            {
                try
                {
                    HttpServer = new HTTPServer(this);
                }
                catch(Exception e)
                {
                    Log("Critical error occurred while starting HTTP server.", true);
                    Log(e.Message, true);
                    Log(e.StackTrace);
                    HttpServer = null;
                }
            }
        }
        else
        {
            if (HttpServer != null)
            {
                HttpServer.Dispose();
                HttpServer = null;
            }
        }
    }

    private void TerritoryChangedEvent(object sender, ushort e)
    {
        Phase = 1;
        if (SFind != null)
        {
            SFind = null;
            Notify("Search stopped", NotificationType.Info);
        }
        for (var i = dynamicElements.Count - 1; i >= 0; i--)
        {
            var de = dynamicElements[i];
            foreach(var l in de.Layouts)
            {
                if (l.UseTriggers)
                {
                    foreach (var t in l.Triggers)
                    {
                        if (t.ResetOnTChange)
                        {
                            t.FiredState = 0;
                            l.TriggerCondition = 0;
                            t.EnableAt.Clear();
                            t.DisableAt.Clear();
                        }
                    }
                }
            }
            foreach (var dt in de.DestroyTime)
            {
                if (dt == (long)DestroyCondition.TERRITORY_CHANGE)
                {
                    dynamicElements.RemoveAt(i);
                }
            }
        }
        foreach(var l in Config.Layouts.Values)
        {
            if (l.UseTriggers)
            {
                foreach(var t in l.Triggers)
                {
                    if (t.ResetOnTChange)
                    {
                        t.FiredState = 0;
                        l.TriggerCondition = 0;
                        t.EnableAt.Clear();
                        t.DisableAt.Clear();
                    }
                }
            }
        }
    }

    
    public void Tick(Framework framework)
    {
        if (Profiler.Enabled) Profiler.MainTick.StartTick();
        try
        {
            LayoutAmount = 0;
            ElementAmount = 0;
            if (LogObjects && Svc.ClientState.LocalPlayer != null)
            {
                foreach(var t in Svc.Objects)
                {
                    var ischar = t is Character;
                    var obj = (t.Name.ToString(), t.ObjectId, t.DataId, ischar ? MemoryManager.GetModelId((Character)t) : 0, t.ObjectKind);
                    loggedObjectList.TryAdd(obj, new ObjectInfo());
                    loggedObjectList[obj].ExistenceTicks++;
                    loggedObjectList[obj].IsChar = ischar;
                    if (ischar)
                    {
                        loggedObjectList[obj].Targetable = MemoryManager.GetIsTargetable((Character)t);
                        loggedObjectList[obj].Visible = MemoryManager.GetIsVisible((Character)t);
                        if (loggedObjectList[obj].Targetable) loggedObjectList[obj].TargetableTicks++;
                        if (loggedObjectList[obj].Visible) loggedObjectList[obj].VisibleTicks++;
                    }
                    else
                    {
                        loggedObjectList[obj].Targetable = MemoryManager.GetIsTargetable(t);
                        if (loggedObjectList[obj].Targetable) loggedObjectList[obj].TargetableTicks++;
                    }
                    loggedObjectList[obj].Distance = Vector3.Distance(Svc.ClientState.LocalPlayer.Position, t.Position);
                    loggedObjectList[obj].HitboxRadius = t.HitboxRadius;
                }
            }
            if (Profiler.Enabled) Profiler.MainTickDequeue.StartTick();
            if (tickScheduler.TryDequeue(out var action))
            {
                action.Invoke();
            }
            if (Profiler.Enabled)
            {
                Profiler.MainTickDequeue.StopTick();
                Profiler.MainTickPrepare.StartTick();
            }
            PlayerPosCache = null;
            displayObjects.Clear();
            if (Svc.ClientState?.LocalPlayer != null)
            {
                if(ChatMessageQueue.Count > 5 * dequeueConcurrency)
                {
                    dequeueConcurrency++;
                    PluginLog.Information($"Too many queued messages ({ChatMessageQueue.Count}); concurrency increased to {dequeueConcurrency}");
                }
                for(var i = 0; i < dequeueConcurrency; i++)
                {
                    if(ChatMessageQueue.TryDequeue(out var ccm))
                    {
                        PluginLog.Verbose("Dequeued message: " + ccm);
                        CurrentChatMessages.Add(ccm);
                    }
                    else
                    {
                        break;
                    }
                }
                if (CurrentChatMessages.Count > 0) PluginLog.Verbose($"Messages dequeued: {CurrentChatMessages.Count}");
                var pl = Svc.ClientState.LocalPlayer;
                if (Svc.ClientState.LocalPlayer.Address == IntPtr.Zero)
                {
                    Log("Pointer to LocalPlayer.Address is zero");
                    return;
                }
                if (MemoryManager.ErrorCode == 0)
                {
                    CamAngleX = MemoryManager.GetCamAngleX() + Math.PI;
                    if (CamAngleX > Math.PI) CamAngleX -= 2 * Math.PI;
                    CamAngleY = MemoryManager.GetCamAngleY();
                    CamZoom = MemoryManager.GetCamZoom();
                }
                /*Range conversion https://stackoverflow.com/questions/5731863/mapping-a-numeric-range-onto-another
                slope = (output_end - output_start) / (input_end - input_start)
                output = output_start + slope * (input - input_start) */
                CurrentLineSegments = (int)((3f + -0.108108f * (CamZoom - 1.5f)) * Config.lineSegments);

                if (Svc.Condition[ConditionFlag.InCombat])
                {
                    if (CombatStarted == 0)
                    {
                        CombatStarted = Environment.TickCount64;
                    }
                }
                else
                {
                    if (CombatStarted != 0)
                    {
                        CombatStarted = 0;
                        Log("Combat ended event");
                        foreach (var l in Config.Layouts.Values)
                        {
                            if (l.UseTriggers)
                            {
                                foreach (var t in l.Triggers)
                                {
                                    if (t.ResetOnCombatExit)
                                    {
                                        t.FiredState = 0;
                                        l.TriggerCondition = 0;
                                        t.EnableAt.Clear();
                                        t.DisableAt.Clear();
                                    }
                                }
                            }
                        }
                        foreach (var de in dynamicElements)
                        {
                            foreach (var l in de.Layouts)
                            {
                                if (l.UseTriggers)
                                {
                                    foreach (var t in l.Triggers)
                                    {
                                        if (t.ResetOnCombatExit)
                                        {
                                            t.FiredState = 0;
                                            l.TriggerCondition = 0;
                                            t.EnableAt.Clear();
                                            t.DisableAt.Clear();
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                //if (CamAngleY > Config.maxcamY) return;

                if(Profiler.Enabled)
                {
                    Profiler.MainTickPrepare.StopTick();
                    Profiler.MainTickFind.StartTick();
                }

                if (SFind != null)
                {
                    var col = Environment.TickCount64 % 1000 < 500 ? Colors.Red : Colors.Yellow;
                    var findEl = new Element(1)
                    {
                        thicc = 3f,
                        radius = 0f,
                        refActorName = SFind.name,
                        refActorObjectID = SFind.oid,
                        refActorComparisonType = SFind.SearchAttribute,
                        overlayText = "$NAME",
                        overlayVOffset = 1.7f,
                        overlayPlaceholders = true,
                        overlayTextColor = col,
                        color = col,
                        includeHitbox = true,
                        onlyTargetable = !SFind.includeUntargetable,
                        tether = Config.TetherOnFind,
                    };
                    ProcessElement(findEl);
                }

                ProcessS2W();

                if (Profiler.Enabled)
                {
                    Profiler.MainTickFind.StopTick();
                    Profiler.MainTickCalcPresets.StartTick();
                }

                foreach (var i in Config.Layouts.Values)
                {
                    ProcessLayout(i);
                }

                foreach (var e in injectedElements)
                {
                    ProcessElement(e);
                    //PluginLog.Information("Processing type " + e.type + JsonConvert.SerializeObject(e, Formatting.Indented));
                }
                injectedElements.Clear();

                if (Profiler.Enabled)
                {
                    Profiler.MainTickCalcPresets.StopTick();
                    Profiler.MainTickCalcDynamic.StartTick();
                }

                for (var i = dynamicElements.Count - 1; i >= 0; i--)
                {
                    var de = dynamicElements[i];

                    foreach (var dt in de.DestroyTime)
                    {
                        if (dt == (long)DestroyCondition.COMBAT_EXIT)
                        {
                            if (!Svc.Condition[ConditionFlag.InCombat] && prevCombatState)
                            {
                                dynamicElements.RemoveAt(i);
                                continue;
                            }
                        }
                        else if (dt > 0)
                        {
                            if (Environment.TickCount64 > dt)
                            {
                                dynamicElements.RemoveAt(i);
                                continue;
                            }
                        }
                    }
                    foreach (var l in de.Layouts)
                    {
                        ProcessLayout(l);
                    }
                    foreach (var e in de.Elements)
                    {
                        ProcessElement(e);
                    }
                }

                if (Profiler.Enabled) Profiler.MainTickCalcDynamic.StopTick();
            }
            else
            {
                Profiler.MainTickPrepare.StopTick();
            }
            prevCombatState = Svc.Condition[ConditionFlag.InCombat];
            CurrentChatMessages.Clear();
        }
        catch(Exception e)
        {
            Log("Caught exception: "+e.Message);
            Log(e.StackTrace);
        }
        if (Profiler.Enabled) Profiler.MainTick.StopTick();
    }

    private void ProcessLayout(Layout i)
    {
        if (!IsLayoutVisible(i)) return;
        LayoutAmount++;
        foreach (var e in i.Elements.Values.ToArray())
        {
            ProcessElement(e, i);
        }
    }

    internal bool IsNameContainsValue(GameObject a, string value)
    {
        //if (Config.DirectNameComparison)
        {
            return a.Name.ToString().ContainsIgnoreCase(value);
        }
        /*var hash = value.GetHashCode();
        var objectID = MemoryManager.GameObject_GetObjectID(a.Address);
        if (!LookupResultCache.ContainsKey((a.Address, objectID, hash)))
        {
            LookupResultCache.Add((a.Address, objectID, hash), a.Name.ToString().ContainsIgnoreCase(value));
        }
        return LookupResultCache[(a.Address, objectID, hash)];*/
    }


    internal S2WInfo s2wInfo;

    internal void BeginS2W(object cls, string x, string y, string z)
    {
        s2wInfo = new(cls, x, y, z);
    }

    internal void ProcessS2W()
    {
        if (s2wInfo != null)
        {
            var lmbdown = Bitmask.IsBitSet(Native.GetKeyState(0x01), 15);
            var mousePos = ImGui.GetIO().MousePos;
            if (Svc.GameGui.ScreenToWorld(new Vector2(mousePos.X, mousePos.Y), out var worldPos, Config.maxdistance * 5))
            {
                s2wInfo.Apply(worldPos.X, worldPos.Z, worldPos.Y);
            }
            if (!lmbdown && prevMouseState)
            {
                s2wInfo = null;
            }
            prevMouseState = lmbdown;
            if (Environment.TickCount64 % 500 < 250 && s2wInfo != null)
            {
                var coords = s2wInfo.GetValues();
                var x = coords.x;
                var y = coords.y;
                var z = coords.z;
                displayObjects.Add(new DisplayObjectLine(x + 2f, y + 2f, z, x - 2f, y - 2f, z, 2f, Colors.Red));
                displayObjects.Add(new DisplayObjectLine(x - 2f, y + 2f, z, x + 2f, y - 2f, z, 2f, Colors.Red));
            }
        }
    }

    public void InjectElement(Element e)
    {
        injectedElements.Add(e);
    }

    internal void ProcessElement(Element e, Layout i = null)
    {
        if (!e.Enabled) return;
        ElementAmount++;
        float radius = e.radius;
        if (e.type == 0)
        {
            if (i == null || !i.UseDistanceLimit || CheckDistanceCondition(i, e.refX, e.refY, e.refZ))
            {
                draw(e, e.refX, e.refY, e.refZ, radius, 0f);
                if (e.tether)
                {
                    displayObjects.Add(new DisplayObjectLine(e.refX + e.offX,
                        e.refY + e.offY,
                        e.refZ + e.offZ,
                        GetPlayerPositionXZY().X, GetPlayerPositionXZY().Y, GetPlayerPositionXZY().Z,
                        e.thicc, e.color));
                }
            }
        }
        else if (e.type == 1 || e.type == 3)
        {
            if (e.includeOwnHitbox) radius += Svc.ClientState.LocalPlayer.HitboxRadius;
            if (e.refActorType == 1)
            {
                if (e.type == 1)
                {
                    var pointPos = GetPlayerPositionXZY();
                    draw(e, pointPos.X, pointPos.Y, pointPos.Z, radius, e.includeRotation ? Svc.ClientState.LocalPlayer.Rotation : 0f, 
                        e.overlayPlaceholders?Svc.ClientState.LocalPlayer:null);
                }
                else if (e.type == 3)
                {
                    AddRotatedLine(GetPlayerPositionXZY(), Svc.ClientState.LocalPlayer.Rotation, e, radius, 0f);
                    //Svc.Chat.Print(Svc.ClientState.LocalPlayer.Rotation.ToString());
                }
            }
            else if (e.refActorType == 2 && Svc.Targets.Target != null
                && Svc.Targets.Target is BattleNpc)
            {
                if (i == null || !i.UseDistanceLimit || CheckDistanceCondition(i, Svc.Targets.Target.GetPositionXZY()))
                {
                    if (e.includeHitbox) radius += Svc.Targets.Target.HitboxRadius;
                    if (e.type == 1)
                    {
                        draw(e, Svc.Targets.Target.GetPositionXZY().X, Svc.Targets.Target.GetPositionXZY().Y,
                            Svc.Targets.Target.GetPositionXZY().Z, radius, e.includeRotation ? Svc.Targets.Target.Rotation : 0f,
                            e.overlayPlaceholders ? Svc.Targets.Target : null);
                    }
                    else if(e.type == 3)
                    {
                        AddRotatedLine(Svc.Targets.Target.GetPositionXZY(), Svc.Targets.Target.Rotation, e, radius, Svc.Targets.Target.HitboxRadius);
                    }

                    if (e.tether)
                    {
                        displayObjects.Add(new DisplayObjectLine(Svc.Targets.Target.GetPositionXZY().X + e.offX,
                            Svc.Targets.Target.GetPositionXZY().Y + e.offY,
                            Svc.Targets.Target.GetPositionXZY().Z + e.offZ,
                            GetPlayerPositionXZY().X, GetPlayerPositionXZY().Y, GetPlayerPositionXZY().Z,
                            e.thicc, e.color));
                    }
                }
            }
            else if (e.refActorType == 0)
            {
                if (Profiler.Enabled) Profiler.MainTickActorTableScan.StartTick();
                foreach (var a in Svc.Objects)
                {
                    var targetable = MemoryManager.GetIsTargetable(a);
                    if (IsAttributeMatches(e, a)
                            && (!e.onlyTargetable || targetable)
                            && (!e.onlyUnTargetable || !targetable)
                            && (!e.onlyVisible || (a is Character chr && MemoryManager.GetIsVisible(chr))))
                    {
                        if (i == null || !i.UseDistanceLimit || CheckDistanceCondition(i, a.GetPositionXZY()))
                        {
                            var aradius = radius;
                            if (e.includeHitbox) aradius += a.HitboxRadius;
                            if (e.type == 1)
                            {
                                draw(e, a.GetPositionXZY().X, a.GetPositionXZY().Y, a.GetPositionXZY().Z, aradius, 
                                    e.includeRotation ? a.Rotation : 0f,
                                    e.overlayPlaceholders ? a : null);
                            }
                            else if (e.type == 3)
                            {
                                AddRotatedLine(a.GetPositionXZY(), a.Rotation, e, aradius, a.HitboxRadius);
                            }
                            if (e.tether)
                            {
                                displayObjects.Add(new DisplayObjectLine(a.GetPositionXZY().X + e.offX,
                                    a.GetPositionXZY().Y + e.offY,
                                    a.GetPositionXZY().Z + e.offZ,
                                    GetPlayerPositionXZY().X, GetPlayerPositionXZY().Y, GetPlayerPositionXZY().Z,
                                    e.thicc, e.color));
                            }
                        }
                    }
                }
                if (Profiler.Enabled) Profiler.MainTickActorTableScan.StopTick();
            }

        }
        else if (e.type == 2)
        {
            if (e.radius > 0)
            {
                PerpOffset(new Vector2(e.refX, e.refY), new Vector2(e.offX, e.offY), 0f, e.radius, out _, out var p1);
                PerpOffset(new Vector2(e.refX, e.refY), new Vector2(e.offX, e.offY), 0f, -e.radius, out _, out var p2);
                PerpOffset(new Vector2(e.refX, e.refY), new Vector2(e.offX, e.offY), 1f, e.radius, out _, out var p3);
                PerpOffset(new Vector2(e.refX, e.refY), new Vector2(e.offX, e.offY), 1f, -e.radius, out _, out var p4);
                displayObjects.Add(new DisplayObjectRect()
                {
                    l1 = new DisplayObjectLine(p1.X, p1.Y, e.refZ,
                    p2.X, p2.Y, e.refZ,
                    e.thicc, e.color),
                    l2 = new DisplayObjectLine(p3.X, p3.Y, e.offZ,
                    p4.X, p4.Y, e.offZ,
                    e.thicc, e.color)
                });
            }
            else
            {
                if (
                    (
                        i == null || !i.UseDistanceLimit || CheckDistanceToLineCondition(i, e)
                    ) &&
                    (
                    ShouldDraw(e.offX, GetPlayerPositionXZY().X, e.offY, GetPlayerPositionXZY().Y)
                    || ShouldDraw(e.refX, GetPlayerPositionXZY().X, e.refY, GetPlayerPositionXZY().Y)
                    )
                    )
                    displayObjects.Add(new DisplayObjectLine(e.refX, e.refY, e.refZ, e.offX, e.offY, e.offZ, e.thicc, e.color));
            }
        }
        /*else if(e.type == 4)
        {
            if (e.Polygon.Count > 2)
            {
                displayObjects.Add(new DisplayObjectPolygon(e));
            }
        }*/
    }

    bool IsAttributeMatches(Element e, GameObject o)
    {
        if (e.refActorComparisonType == 0 && !string.IsNullOrEmpty(e.refActorName) && (e.refActorName == "*" || IsNameContainsValue(o, e.refActorName))) return true;
        if (e.refActorComparisonType == 1 && o is Character c && MemoryManager.GetModelId(c) == e.refActorModelID) return true;
        if (e.refActorComparisonType == 2 && o.ObjectId == e.refActorObjectID) return true;
        if (e.refActorComparisonType == 3 && o.DataId == e.refActorDataID) return true;
        return false;
    }

    void draw(Element e, float x, float y, float z, float r, float angle, GameObject go = null)
    {
        var cx = x + e.offX;
        var cy = y + e.offY;
        if (e.includeRotation)
        {
            var rotatedPoint = RotatePoint(x, y, -angle + e.AdditionalRotation, new Vector3(x - e.offX, y + e.offY, z));
            cx = rotatedPoint.X;
            cy = rotatedPoint.Y;
        }
        if (!ShouldDraw(cx, GetPlayerPositionXZY().X, cy, GetPlayerPositionXZY().Y)) return;
        if (e.thicc > 0)
        {
            if (r > 0)
            {
                displayObjects.Add(new DisplayObjectCircle(cx, cy, z + e.offZ, r, e.thicc, e.color, e.Filled));
            }
            else
            {
                displayObjects.Add(new DisplayObjectDot(cx, cy, z + e.offZ, e.thicc, e.color));
            }
        }
        if (e.overlayText.Length > 0)
        {
            var text = e.overlayText;
            if (go != null)
            {
                text = text
                    .Replace("$NAME", go.Name.ToString())
                    .Replace("$OBJECTID", $"{go.ObjectId:X8}")
                    .Replace("$DATAID", $"{go.DataId:X8}")
                    .Replace("$MODELID", $"{(go is Character chr ? MemoryManager.GetModelId(chr) : 0):X4}")
                    .Replace("$HITBOXR", $"{go.HitboxRadius:F1}")
                    .Replace("$KIND", $"{go.ObjectKind}")
                    .Replace("\\n", "\n");
            }
            displayObjects.Add(new DisplayObjectText(cx, cy, z + e.offZ + e.overlayVOffset, text, e.overlayBGColor, e.overlayTextColor, e.overlayFScale));
        }
    }

    void AddRotatedLine(Vector3 tPos, float angle, Element e, float aradius, float hitboxRadius)
    {
        if (e.includeRotation)
        {
            if (aradius == 0f)
            {
                var pointA = RotatePoint(tPos.X, tPos.Y,
                    -angle + e.AdditionalRotation, new Vector3(
                    tPos.X + -e.refX,
                    tPos.Y + e.refY,
                    tPos.Z + e.refZ) + new Vector3(e.LineAddHitboxLengthXA ? hitboxRadius : 0f, e.LineAddHitboxLengthYA ? hitboxRadius : 0f, e.LineAddHitboxLengthZA ? hitboxRadius : 0f) + new Vector3(e.LineAddPlayerHitboxLengthXA ? Svc.ClientState.LocalPlayer.HitboxRadius : 0f, e.LineAddPlayerHitboxLengthYA ? Svc.ClientState.LocalPlayer.HitboxRadius : 0f, e.LineAddPlayerHitboxLengthZA ? Svc.ClientState.LocalPlayer.HitboxRadius : 0f));
                var pointB = RotatePoint(tPos.X, tPos.Y,
                    -angle + e.AdditionalRotation, new Vector3(
                    tPos.X + -e.offX,
                    tPos.Y + e.offY,
                    tPos.Z + e.offZ) + new Vector3(e.LineAddHitboxLengthX ? hitboxRadius : 0f, e.LineAddHitboxLengthY ? hitboxRadius : 0f, e.LineAddHitboxLengthZ ? hitboxRadius : 0f) + new Vector3(e.LineAddPlayerHitboxLengthX ? Svc.ClientState.LocalPlayer.HitboxRadius : 0f, e.LineAddPlayerHitboxLengthY ? Svc.ClientState.LocalPlayer.HitboxRadius : 0f, e.LineAddPlayerHitboxLengthZ ? Svc.ClientState.LocalPlayer.HitboxRadius : 0f));
                displayObjects.Add(new DisplayObjectLine(pointA.X, pointA.Y, pointA.Z,
                    pointB.X, pointB.Y, pointB.Z,
                    e.thicc, e.color));
            }
            else
            {
                var pointA = RotatePoint(tPos.X, tPos.Y,
                    -angle + e.AdditionalRotation, new Vector3(
                    tPos.X + -e.refX - aradius,
                    tPos.Y + e.refY,
                    tPos.Z + e.refZ));
                var pointB = RotatePoint(tPos.X, tPos.Y,
                    -angle + e.AdditionalRotation, new Vector3(
                    tPos.X + -e.offX - aradius,
                    tPos.Y + e.offY,
                    tPos.Z + e.offZ));
                var pointA2 = RotatePoint(tPos.X, tPos.Y,
                    -angle + e.AdditionalRotation, new Vector3(
                    tPos.X + -e.refX + aradius,
                    tPos.Y + e.refY,
                    tPos.Z + e.refZ));
                var pointB2 = RotatePoint(tPos.X, tPos.Y,
                    -angle + e.AdditionalRotation, new Vector3(
                    tPos.X + -e.offX + aradius,
                    tPos.Y + e.offY,
                    tPos.Z + e.offZ));
                displayObjects.Add(new DisplayObjectRect()
                {
                    l1 = new DisplayObjectLine(pointA.X, pointA.Y, pointA.Z,
                    pointB.X, pointB.Y, pointB.Z,
                    e.thicc, e.color),
                    l2 = new DisplayObjectLine(pointA2.X, pointA2.Y, pointA2.Z,
                    pointB2.X, pointB2.Y, pointB2.Z,
                    e.thicc, e.color)
                });
            }
        }
        else
        {
            var pointA = new Vector3(
                tPos.X + e.refX,
                tPos.Y + e.refY,
                tPos.Z + e.refZ) + new Vector3(e.LineAddHitboxLengthXA ? hitboxRadius : 0f, e.LineAddHitboxLengthYA ? hitboxRadius : 0f, e.LineAddHitboxLengthZA ? hitboxRadius : 0f) + new Vector3(e.LineAddPlayerHitboxLengthXA ? Svc.ClientState.LocalPlayer.HitboxRadius : 0f, e.LineAddPlayerHitboxLengthYA ? Svc.ClientState.LocalPlayer.HitboxRadius : 0f, e.LineAddPlayerHitboxLengthZA ? Svc.ClientState.LocalPlayer.HitboxRadius : 0f);
            var pointB = new Vector3(
                tPos.X + e.offX,
                tPos.Y + e.offY,
                tPos.Z + e.offZ) + new Vector3(e.LineAddHitboxLengthX ? hitboxRadius : 0f, e.LineAddHitboxLengthY ? hitboxRadius : 0f, e.LineAddHitboxLengthZ ? hitboxRadius : 0f) + new Vector3(e.LineAddPlayerHitboxLengthX ? Svc.ClientState.LocalPlayer.HitboxRadius : 0f, e.LineAddPlayerHitboxLengthY ? Svc.ClientState.LocalPlayer.HitboxRadius : 0f, e.LineAddPlayerHitboxLengthZ ? Svc.ClientState.LocalPlayer.HitboxRadius : 0f);
            displayObjects.Add(new DisplayObjectLine(pointA.X, pointA.Y, pointA.Z,
                pointB.X, pointB.Y, pointB.Z,
                e.thicc, e.color));
        }
    }

    internal bool IsLayoutVisible(Layout i)
    {
        if (!i.Enabled) return false;
        if (i.DisableInDuty && Svc.Condition[ConditionFlag.BoundByDuty]) return false;
        if (i.ZoneLockH.Count > 0 && !i.ZoneLockH.Contains(Svc.ClientState.TerritoryType)) return false;
        if (i.Phase != 0 && i.Phase != this.Phase) return false;
        if (i.JobLock != 0 && !Bitmask.IsBitSet(i.JobLock, (int)Svc.ClientState.LocalPlayer.ClassJob.Id)) return false;
        if ((i.DCond == 1 || i.DCond == 3) && !Svc.Condition[ConditionFlag.InCombat]) return false;
        if ((i.DCond == 2 || i.DCond == 3) && !Svc.Condition[ConditionFlag.BoundByDuty]) return false;
        if (i.DCond == 4 && !(Svc.Condition[ConditionFlag.InCombat]
            || Svc.Condition[ConditionFlag.BoundByDuty])) return false;
        if(i.UseDistanceLimit && i.DistanceLimitType == 0)
        {
            if (Svc.Targets.Target != null)
            {
                var dist = Vector3.Distance(Svc.Targets.Target.GetPositionXZY(), GetPlayerPositionXZY()) - (i.DistanceLimitTargetHitbox ? Svc.Targets.Target.HitboxRadius : 0) - (i.DistanceLimitMyHitbox ? Svc.ClientState.LocalPlayer.HitboxRadius : 0);
                if (!(dist >= i.MinDistance && dist < i.MaxDistance)) return false;
            }
            else
            {
                return false;
            }
        }
        if (i.UseTriggers)
        {
            foreach (var t in i.Triggers)
            {
                if (t.FiredState == 2) continue;
                if (t.Type == 2 || t.Type == 3)
                {
                    foreach (var CurrentChatMessage in CurrentChatMessages)
                    {
                        if (CurrentChatMessage.ContainsIgnoreCase(t.Match))
                        {
                            if (t.Duration == 0)
                            {
                                t.FiredState = 0;
                            }
                            else
                            {
                                t.FiredState = 1;
                                t.DisableAt.Add(Environment.TickCount64 + (int)(t.Duration * 1000) + (int)(t.MatchDelay * 1000));
                            }
                            if (t.MatchDelay != 0)
                            {
                                t.EnableAt.Add(Environment.TickCount64 + (int)(t.MatchDelay * 1000));
                            }
                            else
                            {
                                i.TriggerCondition = t.Type == 2 ? 1 : -1;
                            }
                        }
                    }
                }
                if (t.FiredState == 0 && (t.Type == 0 || t.Type == 1))
                {
                    if (CombatStarted != 0 && Environment.TickCount64 - CombatStarted > t.TimeBegin * 1000)
                    {
                        if (t.Duration == 0)
                        {
                            t.FiredState = 2;
                        }
                        else
                        {
                            t.FiredState = 1;
                            t.DisableAt.Add(Environment.TickCount64 + (int)(t.Duration * 1000));
                        }
                        i.TriggerCondition = t.Type == 0 ? 1 : -1;
                    }
                }
                for (var e = 0; e < t.EnableAt.Count; e++)
                {
                    if (Environment.TickCount64 > t.EnableAt[e])
                    {
                        i.TriggerCondition = t.Type == 2 ? 1 : -1;
                        t.EnableAt.RemoveAt(e);
                        break;
                    }
                }
                for (var e = 0; e < t.DisableAt.Count; e++)
                {
                    if (Environment.TickCount64 > t.DisableAt[e])
                    {
                        t.FiredState = (t.Type == 2 || t.Type == 3) ? 0 : 2;
                        t.DisableAt.RemoveAt(e);
                        i.TriggerCondition = 0;
                        break;
                    }
                }

            }
            if (i.TriggerCondition == -1 || (i.TriggerCondition == 0 && i.DCond == 5)) return false;
        }
        return true;
    }

    public bool CheckDistanceCondition(Layout i, float x, float y, float z)
    {
        return CheckDistanceCondition(i, new Vector3(x, y, z));
    }

    public bool CheckDistanceCondition(Layout i, Vector3 v)
    {
        if (i.DistanceLimitType != 1) return true;
        var dist = Vector3.Distance(v, GetPlayerPositionXZY());
        if (!(dist >= i.MinDistance && dist < i.MaxDistance)) return false;
        return true;
    }

    public bool CheckDistanceToLineCondition(Layout i, Element e)
    {
        if (i.DistanceLimitType != 1) return true;
        var dist = Vector3.Distance(FindClosestPointOnLine(GetPlayerPositionXZY(), new Vector3(e.refX, e.refY, e.refZ), new Vector3(e.offX, e.offY, e.offZ)), GetPlayerPositionXZY());
        if (!(dist >= i.MinDistance && dist < i.MaxDistance)) return false;
        return true;
    }

    public bool ShouldDraw(float x1, float x2, float y1, float y2)
    {
        return ((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2)) < Config.maxdistance * Config.maxdistance;
    }

    public void Log(string s, bool tochat = false)
    {
        if (tochat)
        {
            Svc.Chat.Print("[Splatoon]" + s);
        }
        if (Config.dumplog)
        {
            try { PluginLog.Log(s); } catch (Exception) { }
        }
        var line = DateTimeOffset.Now.ToString() + ": " + s;
        for (var i = 0; i < LogStorage.Length; i++)
        {
            if (LogStorage[i] == null)
            {
                LogStorage[i] = line;
                return;
            }
        }
        for (var i = 1; i < LogStorage.Length; i++)
        {
            LogStorage[i - 1] = LogStorage[i];
        }
        LogStorage[LogStorage.Length - 1] = line;
    }
}
