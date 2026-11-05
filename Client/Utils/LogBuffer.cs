using System.Collections.Concurrent;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.UI;
using HarmonyLib;
using MiyakoCarryService.Client.Patches.BigSurvey;
using UnityEngine;

namespace MiyakoCarryService.Client.Utils
{
    internal sealed class LogBuffer
    {
        internal sealed class RaidUsedInfo
        {
            public int RaidIndex;
            public string LocationId;
            public int GameWorldInstanceId;
            public ConcurrentDictionary<string, int> UsedBrains = new();
            public ConcurrentDictionary<string, int> UsedLayers = new();
            public ConcurrentDictionary<string, int> UsedReasons = new();
            /// <summary>Fork: samples (about one a second) of a carry bot standing still in the open, by the reason its brain was running. Written by C2Sain/OpenGroundWatch.</summary>
            public ConcurrentDictionary<string, int> OpenGroundReasons = new();
            /// <summary>Fork: bot-seconds of a carry bot's view frozen for 8 s or more, by the reason its brain was running. Written by C2Sain/FrozenViewWatch.</summary>
            public ConcurrentDictionary<string, int> FrozenViewReasons = new();
        }

        private readonly ConcurrentDictionary<string, LogEntry> _entries = new();
        private int _newBigSurveyCount = 0;
        private readonly object _raidRecordsLock = new();
        private readonly List<RaidUsedInfo> _raidRecords = new();
        private int _raidIndexCounter = 0;
        private RaidUsedInfo _currentRaidRecord = null;

        public ConcurrentDictionary<string, LogEntry> GetEntries()
        {
            return _entries;
        }

        public int GetLogCount
        {
            get
            {
                return _entries.Count;
            }
        }

        public void BeginRaid(int gameWorldInstanceId)
        {
            lock (_raidRecordsLock)
            {
                if (_currentRaidRecord != null && _currentRaidRecord.GameWorldInstanceId == gameWorldInstanceId)
                {
                    return;
                }

                _raidIndexCounter += 1;
                _currentRaidRecord = new RaidUsedInfo
                {
                    RaidIndex = _raidIndexCounter,
                    LocationId = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.LocationId : "Unknown",
                    GameWorldInstanceId = gameWorldInstanceId,
                };
                _raidRecords.Add(_currentRaidRecord);
                if (_raidRecords.Count > 8)
                {
                    _raidRecords.RemoveAt(0);
                }
            }
        }

        public bool EnsureCurrentRaid(int gameWorldInstanceId)
        {
            lock (_raidRecordsLock)
            {
                if (_currentRaidRecord != null && _currentRaidRecord.GameWorldInstanceId == gameWorldInstanceId)
                {
                    return true;
                }
            }

            BeginRaid(gameWorldInstanceId);
            return true;
        }

        public void AddUsedBrain(string brainName)
        {
            if (_currentRaidRecord == null)
            {
                return;
            }

            _currentRaidRecord.UsedBrains.AddOrUpdate(brainName, _ = 1, (brainName, times) =>
            {
                times += 1;
                return times;
            });
        }

        public void AddUsedLayer(string layerName)
        {
            if (_currentRaidRecord == null)
            {
                return;
            }

            _currentRaidRecord.UsedLayers.AddOrUpdate(layerName, _ = 1, (layerName, times) =>
            {
                times += 1;
                return times;
            });
        }

        public void AddUsedReason(string reason)
        {
            if (_currentRaidRecord == null)
            {
                return;
            }

            _currentRaidRecord.UsedReasons.AddOrUpdate(reason, _ = 1, (reason, times) =>
            {
                times += 1;
                return times;
            });
        }

        /// <summary>Fork: counts one sample of a carry bot standing still in the open under <paramref name="reason"/> (C2Sain/OpenGroundWatch).</summary>
        public void AddOpenGroundReason(string reason)
        {
            if (_currentRaidRecord == null || reason == null)
            {
                return;
            }

            _currentRaidRecord.OpenGroundReasons.AddOrUpdate(reason, 1, (_, times) => times + 1);
        }

        /// <summary>Fork: adds <paramref name="seconds"/> bot-seconds of a frozen carry-bot view under <paramref name="reason"/> (C2Sain/FrozenViewWatch).</summary>
        public void AddFrozenViewReason(string reason, int seconds)
        {
            if (_currentRaidRecord == null || reason == null)
            {
                return;
            }

            _currentRaidRecord.FrozenViewReasons.AddOrUpdate(reason, seconds, (_, times) => times + seconds);
        }

        public List<RaidUsedInfo> GetRaidUsedInfos()
        {
            lock (_raidRecordsLock)
            {
                var snapshot = new List<RaidUsedInfo>(_raidRecords);
                snapshot.Reverse();
                return snapshot;
            }
        }

        public void AddEntryIfNotFull(string condition, string stackTrace)
        {
            _newBigSurveyCount += 1;

            ShowNewInformation();

            _entries.AddOrUpdate(condition + stackTrace, _ => new LogEntry(condition, stackTrace),
                (key, oldLogEntry) =>
                {
                    oldLogEntry.Total += 1;
                    return oldLogEntry;
                }
            );
        }

        public void ShowNewInformation()
        {
            if (Singleton<PreloaderUI>.Instantiated)
            {
                var menuTaskBar = Singleton<PreloaderUI>.Instance.MenuTaskBar;
                var menuTaskBarTraverse = Traverse.Create(menuTaskBar);
                var newInformations = menuTaskBarTraverse.Field<GameObject[]>("_newInformation").Value;
                foreach (var newInformation in newInformations)
                {
                    newInformation.SetActive(true);
                }

                MenuTaskBarAwakePatch.NewBigSurveyCount += _newBigSurveyCount;
                _newBigSurveyCount = 0;
            }
        }
    }

    public sealed class LogEntry
    {
        public string Condition;
        public string StackTrace;
        public int Total = 1;

        public LogEntry(string condition, string stackTrace)
        {
            Condition = condition;
            StackTrace = stackTrace;
        }
    }
}
