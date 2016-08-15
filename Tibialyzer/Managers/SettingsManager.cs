﻿// Copyright 2016 Mark Raasveldt
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//   http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Windows.Forms;
using System.Text;
using System.Data.SQLite;

namespace Tibialyzer {
    public static class SettingsManager {
        private static object lockObject = new object();
        public static List<string> changedSettings = new List<string>();
        public static List<string> newSettings = new List<string>();
        public static Dictionary<string, List<string>> settings = new Dictionary<string, List<string>>();
        private static SQLiteConnection settingsConn;
        private static System.Timers.Timer flushTimer;
        public static void Initialize(string databaseFile) {
            InitializeDatabaseConnection(databaseFile);

            flushTimer = new System.Timers.Timer(1000);
            flushTimer.AutoReset = false;
            flushTimer.Elapsed += (s, e) => {
                ActuallySaveSettings();
            };
        }
        private static void InitializeDatabaseConnection(string databaseFile) {
            settingsConn = new SQLiteConnection(String.Format("Data Source={0};Version=3;", databaseFile));
            settingsConn.Open();

            SQLiteCommand comm = new SQLiteCommand("CREATE TABLE IF NOT EXISTS Settings(key STRING, value STRING);", settingsConn);
            comm.ExecuteNonQuery();
        }


        public static void LoadSettings() {
            lock(lockObject) {
                settings.Clear();

                SQLiteDataReader reader = new SQLiteCommand("SELECT key,value FROM Settings;", settingsConn).ExecuteReader();
                while(reader.Read()) {
                    if (!settings.ContainsKey(reader[0].ToString())) {
                        settings.Add(reader[0].ToString(), reader[1].ToString().Split('\n').ToList());
                    }
                }
            }
        }

        public static void LoadSettingsFile(string settingsFile) {
            string currentSetting = null;
            lock (lockObject) {
                settings.Clear();
                changedSettings.Clear();
                newSettings.Clear();

                SQLiteCommand comm = new SQLiteCommand("DROP TABLE IF EXISTS Settings;", settingsConn);
                comm.ExecuteNonQuery();
                comm = new SQLiteCommand("CREATE TABLE IF NOT EXISTS Settings(key STRING, value STRING);", settingsConn);
                comm.ExecuteNonQuery();

                if (!File.Exists(settingsFile)) {
                    ResetSettingsToDefault();
                    SaveSettings();
                } else {
                    var lines = File.ReadAllLines(settingsFile);
                    foreach (string line in lines) {
                        if (line.Length == 0) continue;
                        if (line[0] == '@') {
                            currentSetting = line.Substring(1, line.Length - 1);
                            if (!settings.ContainsKey(currentSetting))
                                settings.Add(currentSetting, new List<string>());
                        } else if (currentSetting != null) {
                            settings[currentSetting].Add(line);
                        }
                    }
                }
            }
        }

        private static void ActuallySaveSettings() {
            try {
                lock (lockObject) {
                    SQLiteTransaction transaction = settingsConn.BeginTransaction();
                    SQLiteCommand command;

                    foreach (string change in changedSettings) {
                        string key = change;
                        if (settings.ContainsKey(change)) {
                            string value = string.Join("\n", settings[change]).Replace("'", "''");
                            // update
                            command = new SQLiteCommand(String.Format("UPDATE Settings SET value='{0}' WHERE key='{1}'", value, key), settingsConn, transaction);
                            command.ExecuteNonQuery();
                        } else {
                            // delete
                            command = new SQLiteCommand(String.Format("DELETE FROM Settings WHERE key='{0}'", key), settingsConn, transaction);
                            command.ExecuteNonQuery();
                        }
                    }
                    foreach (string change in newSettings) {
                        string key = change;
                        string value = string.Join("\n", settings[change]).Replace("'", "''");
                        // insert
                        command = new SQLiteCommand(String.Format("INSERT INTO Settings (key,value) VALUES ('{0}', '{1}');", key, value), settingsConn, transaction);
                        command.ExecuteNonQuery();
                    }

                    changedSettings.Clear();
                    newSettings.Clear();
                    transaction.Commit();
                }
                if (getSettingBool("AutomaticSettingsBackup")) {
                    CreateBackup();
                }
            } catch (Exception ex) {
                MainForm.mainForm.Invoke((MethodInvoker)delegate {
                    MainForm.mainForm.DisplayWarning(String.Format("Failed to save settings: {0}", ex.Message));
                });
            }
        }

        public static void CreateBackup() {
            lock(lockObject) {
                try {
                    File.Delete(Constants.SettingsTemporaryFile);
                    File.Copy(Constants.SettingsDatabaseFile, Constants.SettingsTemporaryFile);
                    File.Delete(Constants.SettingsBackupFile);
                    File.Move(Constants.SettingsTemporaryFile, Constants.SettingsBackupFile);
                } catch(Exception ex) {
                    MainForm.mainForm.Invoke((MethodInvoker)delegate {
                        MainForm.mainForm.DisplayWarning(String.Format("Failed to create backup: {0}", ex.Message));
                    });
                }
            }
        }

        public static void RestoreBackup() {
            lock(lockObject) {
                if (!File.Exists(Constants.SettingsBackupFile)) {
                    MainForm.mainForm.Invoke((MethodInvoker)delegate {
                        MainForm.mainForm.DisplayWarning(String.Format("Failed to restore backup: no backup file found"));
                    });
                    return;
                }
                try { 
                    SQLiteConnection conn = new SQLiteConnection(String.Format("Data Source={0};Version=3;", Constants.SettingsBackupFile));
                    conn.Open();
                    conn.Close();
                } catch(Exception ex) {
                    // backup database not a valid settings file
                    MainForm.mainForm.Invoke((MethodInvoker)delegate {
                        MainForm.mainForm.DisplayWarning(String.Format("Failed to restore backup: {0}", ex.Message));
                    });
                    return;
                }
                settingsConn.Close();
                try {
                    File.Delete(Constants.SettingsTemporaryFile);
                    File.Copy(Constants.SettingsDatabaseFile, Constants.SettingsTemporaryFile);
                } catch(Exception ex) {
                    MainForm.mainForm.Invoke((MethodInvoker)delegate {
                        MainForm.mainForm.DisplayWarning(String.Format("Failed to create a copy of the current settings file: {0}", ex.Message));
                    });
                    return;
                }
                try {
                    File.Delete(Constants.SettingsDatabaseFile);
                    File.Copy(Constants.SettingsBackupFile, Constants.SettingsDatabaseFile);
                    InitializeDatabaseConnection(Constants.SettingsDatabaseFile);
                    LoadSettings();
                    File.Delete(Constants.SettingsTemporaryFile);
                } catch(Exception ex) {
                    string message = ex.Message;
                    try {
                        // failed to restore backup, restore old settings file
                        File.Move(Constants.SettingsTemporaryFile, Constants.SettingsDatabaseFile);
                    } catch(Exception ex2) {
                        Console.WriteLine(ex2.Message);
                    }
                    MainForm.mainForm.Invoke((MethodInvoker)delegate {
                        MainForm.mainForm.DisplayWarning(String.Format("Failed to restore backup: {0}", ex.Message));
                    });
                    return;
                }
                // restart after a successful backup
                Application.Restart();
            }
        }
        
        public static void SaveSettings() {
            flushTimer.Stop();
            flushTimer.Start();
        }

        public static void removeSetting(string key) {
            lock(lockObject) {
                if (settings.ContainsKey(key)) {
                    settings.Remove(key);
                    changedSettings.Add(key);
                }
            }
            SaveSettings();
        }

        public static bool getSettingBool(string key) {
            string str = getSettingString(key);
            if (str == null) return false;
            return str == "True";
        }

        public static int getSettingInt(string key) {
            string str = getSettingString(key);
            if (str == null) return -1;
            int v;
            if (int.TryParse(str, out v)) {
                return v;
            }
            return -1;
        }
        public static double getSettingDouble(string key) {
            string str = getSettingString(key);
            if (str == null) return -1;
            double v;
            if (double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) {
                return v;
            }
            return -1;
        }

        public static string getSettingString(string key) {
            var settings = getSetting(key);
            if (settings.Count == 0) return null;
            return settings[0];
        }

        public static List<string> getSetting(string key) {
            lock(lockObject) {
                if (!settings.ContainsKey(key)) return new List<string>();
                return settings[key];
            }
        }

        public static void setSetting(string key, List<string> value) {
            lock(lockObject) {
                if (!settings.ContainsKey(key)) {
                    settings.Add(key, value);
                    newSettings.Add(key);
                } else {
                    settings[key] = value;
                    changedSettings.Add(key);
                }
            }
            SaveSettings();
        }

        public static void setSetting(string key, string value) {
            setSetting(key, new List<string> { value });
        }

        public static void setSetting(string key, int value) {
            setSetting(key, value.ToString());
        }
        public static void setSetting(string key, bool value) {
            setSetting(key, value.ToString());
        }
        public static void setSetting(string key, double value) {
            setSetting(key, value.ToString(CultureInfo.InvariantCulture));
        }

        public static void setSettingIfNotSet(string key, List<string> value) {
            lock (lockObject) { 
                if (!settings.ContainsKey(key)) settings.Add(key, value);
            }
        }

        public static void setSettingIfNotSet(string key, string value) {
            lock (lockObject) {
                if (!settings.ContainsKey(key)) setSetting(key, new List<string> { value });
            }
        }

        public static void setSettingIfNotSet(string key, int value) {
            lock (lockObject) {
                if (!settings.ContainsKey(key)) setSetting(key, value.ToString());
            }
        }
        public static void setSettingIfNotSet(string key, bool value) {
            lock (lockObject) {
                if (!settings.ContainsKey(key)) setSetting(key, value.ToString());
            }
        }
        public static void setSettingIfNotSet(string key, double value) {
            lock (lockObject) {
                if (!settings.ContainsKey(key)) setSetting(key, value.ToString(CultureInfo.InvariantCulture));
            }
        }

        public static bool settingExists(string key) {
            lock (lockObject) {
                return settings.ContainsKey(key) && settings[key].Count > 0;
            }
        }

        public const string defaultWASDSettings = @"; Suspend autohotkey mode with Ctrl+Enter
Ctrl+Enter::Suspend
; Enable WASD Movement
W::Up
A::Left
S::Down
D::Right
; Enable diagonal movement with QEZC
Q::NumpadHome
E::NumpadPgUp
Z::NumpadEnd
C::NumpadPgDn
; Hotkey Tibialyzer commands
; Open loot window with the [ key
[::Command=loot@
; Show exp with ] key
]::Command=exp@
; Close all windows when = key is pressed
=::Command=close@
; Open last window with - key
-::Command=refresh@
";

        public static void ApplyDefaultSettings() {
            setSettingIfNotSet("EnableEventNotifications", true);
            setSettingIfNotSet("EnableUnrecognizedNotifications", true);
            setSettingIfNotSet("CopyAdvances", true);
            setSettingIfNotSet("UseRichNotificationType", true);
            setSettingIfNotSet("LookMode", true);
            setSettingIfNotSet("StartAutohotkeyAutomatically", false);
            setSettingIfNotSet("ShutdownAutohotkeyOnExit", false);
            setSettingIfNotSet("NotificationItems", "");
            setSettingIfNotSet("AutoHotkeySettings", defaultWASDSettings.Split(new string[] { "\r\n" }, StringSplitOptions.None).ToList());
            setSettingIfNotSet("AutoScreenshotAdvance", false);
            setSettingIfNotSet("AutoScreenshotItemDrop", false);
            setSettingIfNotSet("AutoScreenshotDeath", false);
            setSettingIfNotSet("EnableScreenshots", false);
            setSettingIfNotSet("Names", "Mytherin");
            setSettingIfNotSet("ScanSpeed", "50");
            setSettingIfNotSet("OutfitGenderMale", true);
            setSettingIfNotSet("RichNotificationXOffset", 30);
            setSettingIfNotSet("RichNotificationYOffset", 30);
            setSettingIfNotSet("RichNotificationAnchor", 0);
            setSettingIfNotSet("SimpleNotificationXOffset", 5);
            setSettingIfNotSet("SimpleNotificationYOffset", 10);
            setSettingIfNotSet("SimpleNotificationAnchor", 3);
            setSettingIfNotSet("EnableSimpleNotificationAnimation", true);
            setSettingIfNotSet("SuspendedNotificationXOffset", 10);
            setSettingIfNotSet("SuspendedNotificationYOffset", 10);
            setSettingIfNotSet("SuspendedNotificationAnchor", 1);
            setSettingIfNotSet("TibiaClientName", "Tibia");
            foreach (string obj in Constants.NotificationTypes) {
                string settingObject = obj.Replace(" ", "");
                setSettingIfNotSet(settingObject + "Anchor", 0);
                setSettingIfNotSet(settingObject + "XOffset", 30);
                setSettingIfNotSet(settingObject + "YOffset", 30);
                setSettingIfNotSet(settingObject + "Duration", 30);
                setSettingIfNotSet(settingObject + "Group", 0);
            }
            setSettingIfNotSet("StackAllItems", false);
            setSettingIfNotSet("IgnoreLowExperience", false);
            setSettingIfNotSet("IgnoreLowExperienceValue", 250);
            setSettingIfNotSet("AutomaticallyWriteLootToFile", true);
            setSettingIfNotSet("NotificationConditions", "item.value >= 2000");
            setSettingIfNotSet("CityDisplayFormWidth", 396);
            setSettingIfNotSet("CreatureDropsFormWidth", 358);
            setSettingIfNotSet("CreatureStatsFormWidth", 378);
            setSettingIfNotSet("DamageChartWidth", 450);
            setSettingIfNotSet("ExperienceChartWidth", 450);
            setSettingIfNotSet("SummaryFormWidth", 210);
            setSettingIfNotSet("ItemViewFormWidth", 378);
            setSettingIfNotSet("PopupDuration", 8);
            setSettingIfNotSet("SummaryMaxItemDrops", 5);
            setSettingIfNotSet("SummaryMaxCreatures", 5);
            setSettingIfNotSet("SummaryMaxRecentDrops", 5);
            setSettingIfNotSet("SummaryMaxDamagePlayers", 5);
            setSettingIfNotSet("SummaryMaxUsedItems", 5);
            setSettingIfNotSet("ExperiencePerHourCalculation", "TibiaStyle");
            setSettingIfNotSet("SimpleNotificationWidth", 354);
            setSettingIfNotSet("SimpleNotificationCopyButton", true);
            setSettingIfNotSet("ScanInternalTabStructure", true);
            setSettingIfNotSet("SkipDuplicateLoot", false);
                        
            setSettingIfNotSet("ManaBarYOffset", 95);
            setSettingIfNotSet("ExperienceBarYOffset", 160);

            setSettingIfNotSet("CurvedBarsWidth", 300);
            setSettingIfNotSet("CurvedBarsHeight", 300);
            setSettingIfNotSet("CurvedBarsAnchor", 4);
            setSettingIfNotSet("CurvedBarsXOffset", -100);
            setSettingIfNotSet("CurvedBarsYOffset", -100);
            setSettingIfNotSet("PortraitAnchor", 0);
            setSettingIfNotSet("PortraitXOffset", 300);
            setSettingIfNotSet("PortraitYOffset", 20);
            setSettingIfNotSet("PortraitWidth", 300);
            setSettingIfNotSet("PortraitHeight", 200);

            foreach (string obj in Constants.HudTypes) {
                string settingObject = obj.Replace(" ", "");
                setSettingIfNotSet(settingObject + "Anchor", 1);
                setSettingIfNotSet(settingObject + "XOffset", 180);
                setSettingIfNotSet(settingObject + "YOffset", 30);
                setSettingIfNotSet(settingObject + "FontSize", 20);
                setSettingIfNotSet(settingObject + "Width", 200);
                setSettingIfNotSet(settingObject + "Height", 65);
                setSettingIfNotSet(settingObject + "ShowOnStartup", false);
                setSettingIfNotSet(settingObject + "Opacity", 0.8);
                setSettingIfNotSet(settingObject + "DisplayText", true);
            }
            setSettingIfNotSet("HealthListDisplayNames", true);
            setSettingIfNotSet("HealthListDisplayIcons", false);
            setSettingIfNotSet("HealthListPlayerList", "");

            setSettingIfNotSet("NotificationShowTibiaActive", false);
            setSettingIfNotSet("MonitorAnchor", 0);
            setSettingIfNotSet("SummaryLootItemSize", 25);
            setSettingIfNotSet("SummaryRecentDropsItemSize", 25);
            setSettingIfNotSet("SummaryWasteItemSize", 25);
            setSettingIfNotSet("MaxDamageChartPlayers", 0);

            setSettingIfNotSet("PortraitBackgroundScale", 100);
            setSettingIfNotSet("PortraitBackgroundXOffset", 0);
            setSettingIfNotSet("PortraitBackgroundYOffset", 0);
            setSettingIfNotSet("PortraitCenterScale", 80);
            setSettingIfNotSet("PortraitCenterXOffset", 0);
            setSettingIfNotSet("PortraitCenterYOffset", 0);

            setSettingIfNotSet("GatherExtraPlayerInformation", false);

            setSettingIfNotSet("TibialyzerLanguage", "en-US");
            setSettingIfNotSet("AutomaticallyDetectCharacter", true);
            setSettingIfNotSet("AutomaticallyDownloadAddresses", true);
            setSettingIfNotSet("TibiaClient", "Classic");
        }

        public static void ResetSettingsToDefault() {
            lock (lockObject) {
                settings = new Dictionary<string, List<string>>();
            }
            
            ApplyDefaultSettings();
            SaveSettings();
        }
    }
}
