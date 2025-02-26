﻿using Dalamud.Game.Command;
using Dalamud.Game.Gui.Toast;
using Dalamud.Interface.Internal.Notifications;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Splatoon
{
    class Commands : IDisposable
    {
        Splatoon p;
        internal Commands(Splatoon p)
        {
            this.p = p;
            Svc.Commands.AddHandler("/splatoon", new CommandInfo(delegate (string command, string arguments)
            {
                if (arguments == "")
                {
                    p.ConfigGui.Open = true;
                }
                else if (arguments.StartsWith("enable "))
                {
                    try
                    {
                        var name = arguments.Substring(arguments.IndexOf("enable ") + 7);
                        SwitchState(name, true);
                    }
                    catch (Exception e)
                    {
                        p.Log(e.Message);
                    }
                }
                else if (arguments.StartsWith("disable "))
                {
                    try
                    {
                        var name = arguments.Substring(arguments.IndexOf("disable ") + 8);
                        SwitchState(name, false);
                    }
                    catch (Exception e)
                    {
                        p.Log(e.Message);
                    }
                }
                else if (arguments.StartsWith("settarget "))
                {
                    try
                    {
                        if (Svc.Targets.Target == null)
                        {
                            Notify("Target not selected", NotificationType.Error);
                        }
                        else 
                        {
                            var name = arguments.Substring(arguments.IndexOf("settarget ") + 10).Split('~');
                            var el = p.Config.Layouts[name[0]].Elements[name[1]];
                            el.refActorName = Svc.Targets.Target.Name.ToString();
                            el.refActorDataID = Svc.Targets.Target.DataId;
                            el.refActorObjectID = Svc.Targets.Target.ObjectId;
                            if (Svc.Targets.Target is Character c) el.refActorModelID = (uint)p.MemoryManager.GetModelId(c);
                            Notify("Successfully set target", NotificationType.Success);
                        }
                    }
                    catch (Exception e)
                    {
                        p.Log(e.Message);
                    }
                }
                else if(arguments.StartsWith("floodchat "))
                {
                    int a = 2;
                    Safe(delegate
                    {
                        for(var i = 0;i<uint.Parse(arguments.Replace("floodchat ", "")); i++)
                        {
                            Svc.Chat.Print(new string(Enumerable.Repeat("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", 30).Select(s => s[new Random().Next(30)]).ToArray()));
                        }
                    });
                }
            })
            {
                HelpMessage = "open Splatoon configuration menu \n" +
                "/splatoon disable <PresetName> → disable specified preset \n" +
                "/splatoon enable <PresetName> → enable specified preset"
            });

            Svc.Commands.AddHandler("/sf", new CommandInfo(delegate (string command, string arguments)
            {
                if (arguments == "")
                {
                    if (p.SFind != null)
                    {
                        Notify("Search stopped", NotificationType.Info);
                        p.SFind = null;
                    }
                    else
                    {
                        Notify("Please specify target name", NotificationType.Error);
                    }
                }
                else
                {
                    p.SFind = new()
                    {
                        name = arguments.Trim(),
                        includeUntargetable = arguments.StartsWith("!!")
                    };
                    if (p.SFind.includeUntargetable)
                    {
                        p.SFind.name = arguments[2..];
                    }
                    Notify("Searching for: " + p.SFind.name + (p.SFind.includeUntargetable?" (+untargetable)":""), NotificationType.Success);
                }
            })
            {
                HelpMessage = "highlight objects containing specified phrase"
            });
        }

        internal void SwitchState(string name, bool enable, bool web = false)
        {
            try
            {
                if (name.Contains("~"))
                {
                    var aname = name.Split('~');
                    if (web && p.Config.Layouts[aname[0]].DisableDisabling) return;
                    p.Config.Layouts[aname[0]].Elements[aname[1]].Enabled = enable;
                }
                else
                {
                    if (web && p.Config.Layouts[name].DisableDisabling) return;
                    p.Config.Layouts[name].Enabled = enable;
                }
            }
            catch(Exception e)
            {
                p.Log(e.Message, true);
                p.Log(e.StackTrace);
            }
        }

        public void Dispose()
        {
            Svc.Commands.RemoveHandler("/splatoon");
            Svc.Commands.RemoveHandler("/sf");
        }
    }
}
