using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DrakiaXYZ.BigBrain.Brains;
using EFT;

namespace MiyakoCarryService.Client.Utils
{
    /// <summary>
    /// 运行时按单个 BotOwner 实时增删/激活/恢复 BigBrain 自定义 Layer，绕过 BrainManager 的全局 brainNames 匹配，直接操作目标 Bot 的大脑。
    /// </summary>
    internal static class LayerUtils
    {
        private static int _currentLayerId = 15156;

        private static Type _customLayerWrapperType;
        private static bool _initialized = false;

        private static readonly Dictionary<string, Dictionary<string, int>> _injectedLayers = new();
        private static readonly Dictionary<string, Dictionary<string, (int Index, AICoreLayer<BotLogicDecision> Layer)>> _excludedLayers = new();

        private static ConcurrentDictionary<Type, int> _customLayerMaps;

        private static void EnsureInit()
        {
            if (_initialized)
            {
                return;
            }

            _customLayerWrapperType = typeof(BrainManager).Assembly.GetType("DrakiaXYZ.BigBrain.Internal.CustomLayerWrapper");
            _initialized = true;
        }

        private static HashSet<string> GetBrainLayerNames(BotOwner botOwner)
        {
            var names = new HashSet<string>();
            if (botOwner == null || botOwner.Brain?.BaseBrain == null)
            {
                return names;
            }

            var dict = botOwner.Brain.BaseBrain._layers;
            foreach (var index in dict.Keys.ToList())
            {
                names.Add(dict[index].Name());
            }
            return names;
        }

        public static bool IsMcsBotPlayerInjected(BotOwner botOwner)
        {
            if (botOwner == null || botOwner.Brain?.BaseBrain == null || _customLayerMaps == null)
            {
                return false;
            }

            var brainLayerNames = GetBrainLayerNames(botOwner);
            var map = _injectedLayers.TryGetValue(botOwner.ProfileId, out var _map) ? _map : (_injectedLayers[botOwner.ProfileId] = new());

            var allInjected = true;
            foreach (var customLayerType in _customLayerMaps.Keys)
            {
                var layerName = customLayerType.Name;
                if (!brainLayerNames.Contains(layerName))
                {
                    map.Remove(layerName);
                    allInjected = false;
                }
            }
            return allInjected;
        }

        public static void RegisterCustomLayer(Type customLayerType, int priority)
        {
            if (_customLayerMaps == null)
            {
                _customLayerMaps = new();
            }

            _customLayerMaps.AddOrUpdate(customLayerType, priority,
                (customLayerType, oldPriority) =>
                {
                    oldPriority = priority;
                    return oldPriority;
                }
            );
        }

        public static void OnRaidEnded()
        {
            _injectedLayers.Clear();
            _excludedLayers.Clear();
        }

        public static ConcurrentDictionary<Type, int> GetCustomLayerMaps()
        {
            return _customLayerMaps;
        }

        public static bool McsAddCustomLayer(BotOwner botOwner, Type customLayerType, int priority)
        {
            if (botOwner == null || botOwner.IsDead || botOwner.Brain?.BaseBrain == null)
            {
                return false;
            }

            EnsureInit();

            var layerName = customLayerType.Name;
            var map = _injectedLayers.TryGetValue(botOwner.ProfileId, out var _map) ? _map : (_injectedLayers[botOwner.ProfileId] = new());
            if (map.ContainsKey(layerName))
            {
                if (GetBrainLayerNames(botOwner).Contains(layerName))
                {
                    return false;
                }
                map.Remove(layerName);
            }

            try
            {
                var wrapper = (AICoreLayer<BotLogicDecision>)Activator.CreateInstance(_customLayerWrapperType, [customLayerType, botOwner, priority]);

                var layerId = _currentLayerId++;
                if (!botOwner.Brain.BaseBrain.TryAddLayer(layerId, wrapper, true))
                {
                    return false;
                }

                map[layerName] = layerId;
                return true;
            }
            catch (Exception e)
            {
                McsLogger.LogError(e);
                return false;
            }
        }

        public static bool McsRemoveLayer(BotOwner botOwner, string layerName)
        {
            if (botOwner == null || botOwner.Brain?.BaseBrain == null)
            {
                return false;
            }

            var baseBrain = botOwner.Brain.BaseBrain;
            var dict = baseBrain._layers;

            foreach (var index in dict.Keys.ToList())
            {
                if (dict[index].Name() != layerName)
                {
                    continue;
                }

                var layer = dict[index];
                baseBrain.DeactivateLayer(index);
                dict.Remove(index);

                var excluded = _excludedLayers.TryGetValue(botOwner.ProfileId, out var em) ? em : (_excludedLayers[botOwner.ProfileId] = new());
                excluded[layerName] = (index, layer);

                if (_injectedLayers.TryGetValue(botOwner.ProfileId, out var im))
                {
                    im.Remove(layerName);
                }
                return true;
            }
            return false;
        }

        public static void McsRemoveLayers(BotOwner botOwner, IEnumerable<string> layerNames)
        {
            if (botOwner == null || botOwner.Brain?.BaseBrain == null || layerNames == null)
            {
                return;
            }

            foreach (var layerName in layerNames)
            {
                McsRemoveLayer(botOwner, layerName);
            }
        }

        public static bool McsRestoreLayer(BotOwner botOwner, string layerName)
        {
            if (botOwner == null || botOwner.Brain?.BaseBrain == null)
            {
                return false;
            }

            if (!_excludedLayers.TryGetValue(botOwner.ProfileId, out var excluded) || !excluded.TryGetValue(layerName, out var cached))
            {
                return false;
            }

            var baseBrain = botOwner.Brain.BaseBrain;
            if (baseBrain._layers.ContainsKey(cached.Index))
            {
                return false;
            }

            if (!baseBrain.TryAddLayer(cached.Index, cached.Layer, true))
            {
                return false;
            }

            excluded.Remove(layerName);
            var map = _injectedLayers.TryGetValue(botOwner.ProfileId, out var m) ? m : (_injectedLayers[botOwner.ProfileId] = new());
            map[layerName] = cached.Index;
            return true;
        }

        public static void McsRestoreLayers(BotOwner botOwner, IEnumerable<string> layerNames)
        {
            if (botOwner == null || botOwner.Brain?.BaseBrain == null || layerNames == null)
            {
                return;
            }

            foreach (var layerName in layerNames)
            {
                McsRestoreLayer(botOwner, layerName);
            }
        }

        public static void McsRemoveNonKeepLayers(BotOwner botOwner, IEnumerable<string> explicitKeepLayerNames)
        {
            if (botOwner == null || botOwner.Brain?.BaseBrain == null)
            {
                return;
            }

            var keepNames = new HashSet<string>(explicitKeepLayerNames ?? Array.Empty<string>());
            if (_customLayerMaps != null)
            {
                foreach (var customLayerType in _customLayerMaps.Keys)
                {
                    keepNames.Add(customLayerType.Name);
                }
            }

            foreach (var layerName in GetBrainLayerNames(botOwner))
            {
                if (keepNames.Contains(layerName) || MiyakoCarryService.Client.C2Sain.C2Gate.ShouldKeepLayer(layerName))
                {
                    continue;
                }
                McsRemoveLayer(botOwner, layerName);
            }
        }

        public static void McsRestoreAllExcludedLayers(BotOwner botOwner)
        {
            if (botOwner == null || botOwner.Brain?.BaseBrain == null)
            {
                return;
            }

            if (!_excludedLayers.TryGetValue(botOwner.ProfileId, out var excluded))
            {
                return;
            }

            foreach (var layerName in excluded.Keys.ToList())
            {
                McsRestoreLayer(botOwner, layerName);
            }
        }

        public static bool McsHasLayer(BotOwner botOwner, string layerName)
        {
            return botOwner != null && GetBrainLayerNames(botOwner).Contains(layerName);
        }
    }
}