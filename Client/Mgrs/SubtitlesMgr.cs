
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.UI;
using HarmonyLib;
using MiyakoCarryService.Client.Events;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Models;
using MiyakoCarryService.Client.Utils;
using SPT.Common.Utils;
using TMPro;
using UnityEngine;

namespace MiyakoCarryService.Client.Mgrs
{
    public class SubtitlesMgr : BaseMgr
    {
        private GameObject _mcsDialogScreen;
        private Transform _subsContainer;
        private GameObject _subtitlesViewTemplate;
        private Dictionary<MongoID, Subtitles> _subTitles = new();
        private Dictionary<EPhraseTrigger, string> _talkContents;
        private Dictionary<EPhraseTrigger, Func<string, McsMsg, Player, Player, string>> _phraseHandleMaps;
        private McsMgr McsMgr => field ??= MgrAccessor.Get<McsMgr>();

        public override void Start()
        {
            base.Start();
            _talkContents = new()
            {
                { EPhraseTrigger.None, "未知的回应，应进行反馈。" },
                { EPhraseTrigger.OnFirstContact, Locales.ONFIRSTCONTACT },
                { EPhraseTrigger.Roger, Locales.ROGER },
                { EPhraseTrigger.OnPosition, Locales.ONPOSITION },
                { EPhraseTrigger.GoLoot, Locales.GOLOOT },
                { EPhraseTrigger.OnLoot, Locales.ONLOOT },
                { EPhraseTrigger.LootGeneric, Locales.LOOTGENERIC },
                { EPhraseTrigger.Clear, Locales.CLEAR },
                { EPhraseTrigger.LeftFlank, Locales.LEFTFLANK },
                { EPhraseTrigger.RightFlank, Locales.RIGHTFLANK },
                { EPhraseTrigger.InTheFront, Locales.INTHEFRONT },
                { EPhraseTrigger.OnSix, Locales.ONSIX },
                { EPhraseTrigger.EnemyDown, Locales.ENEMYDOWN },
                { EPhraseTrigger.Going, Locales.GOING },
                { EPhraseTrigger.HoldPosition, Locales.HOLDPOSITION },
                { EPhraseTrigger.Regroup, Locales.REGROUP },
                { EPhraseTrigger.StartHeal, Locales.STARTHEAL },
                { EPhraseTrigger.OnFriendlyDown, Locales.ONFRIENDLYDOWN },
                { EPhraseTrigger.FollowMe, Locales.FOLLOWME },
                { EPhraseTrigger.Negative, Locales.NEGATIVE },
                { EPhraseTrigger.Mine, Locales.MINE },
                { EPhraseTrigger.OnFight, Locales.ONFIGHT },
                { EPhraseTrigger.OnOutOfAmmo, Locales.ONOUTOFAMMO },
                // Fork: voice lines the C2/SAIN fork's replies use (text in C2Sain/ForkText.cs).
                { EPhraseTrigger.Gogogo, "McsFork/Talk Gogogo" },
                { EPhraseTrigger.Covering, "McsFork/Talk Covering" },
                { EPhraseTrigger.LostVisual, "McsFork/Talk LostVisual" },
                // 空短语、临时内容，用于传递任意Key实现任何对话内容
                { EPhraseTrigger.PhraseNone, "PhraseNone" }
            };

            _phraseHandleMaps = new()
            {
                { EPhraseTrigger.OnFirstContact, HandleOnFirstContact },
                { EPhraseTrigger.OnLoot, HandleOnLoot },
                { EPhraseTrigger.LootGeneric, HandleLootGeneric },
                { EPhraseTrigger.PhraseNone, HandlePhraseNone },
                { EPhraseTrigger.Mine, HandlePhraseMine },
                { EPhraseTrigger.Going, HandlePhraseWithOnFight },
                { EPhraseTrigger.Negative, HandlePhraseWithOnFight },
                // Fork: the fork's replies carry their own text as keys; upstream's keyless uses keep their text.
                { EPhraseTrigger.Roger, C2Sain.ForkCommands.ReplaceWithKeys },
                { EPhraseTrigger.Gogogo, C2Sain.ForkCommands.ReplaceWithKeys },
                { EPhraseTrigger.Covering, C2Sain.ForkCommands.ReplaceWithKeys },
                { EPhraseTrigger.LostVisual, C2Sain.ForkCommands.ReplaceWithKeys },
            };

            TasksExtensions.HandleExceptions(Init());
        }

        public override void OnMgrDestroy()
        {
            base.OnMgrDestroy();
            if (_mcsDialogScreen != null)
            {
                Destroy(_mcsDialogScreen);
            }
            _subsContainer = null;
        }

        private async Task Init()
        {
            while (true)
            {
                await Task.Delay(1000);
                if (Singleton<CommonUI>.Instantiated || Singleton<GameWorld>.Instantiated)
                {
                    var traderDialogScreen = Singleton<CommonUI>.Instance.TraderDialogScreen;
                    var oldTraderDialogScreenGameObject = traderDialogScreen.gameObject;
                    _mcsDialogScreen = Instantiate(oldTraderDialogScreenGameObject, Singleton<CommonUI>.Instance.transform.GetChild(0));
                    var _traderDialogScreen = _mcsDialogScreen.transform.GetComponentInChildren<TraderDialogScreen>();
                    Destroy(_traderDialogScreen);
                    _mcsDialogScreen.name = "Mcs Subtitles Screen";
                    for (int i = 0; i < _mcsDialogScreen.transform.childCount; i++)
                    {
                        var childContainer = _mcsDialogScreen.transform.GetChild(i);
                        if (childContainer.transform.name == "SubsContainer")
                        {
                            _subsContainer = childContainer;
                            break;
                        }
                    }

                    var subtitlesView = _subsContainer.GetChild(0).GetComponent<SubtitlesView>();
                    _subtitlesViewTemplate = subtitlesView.gameObject;
                    _subtitlesViewTemplate.transform.name = "McsBotPlayerSubtitles";
                    _mcsDialogScreen.SetActive(true);
                    subtitlesView.HideGameObject();
                    break;
                }
            }
        }

        public void TalkMsg(Player mcsLeadPlayer, Player mcsBotPlayer, McsMsg msg)
        {
            if (MiyakoCarryServicePlugin.FikaInstalled && Tools.IsHost)
            {
                var myPlayer = Singleton<GameWorld>.Instance.MainPlayer;
                if (myPlayer != null && McsMgr.IsMyMcsBotPlayer(myPlayer.ProfileId, mcsBotPlayer.ProfileId))
                {
                    if (!ShouldSilencePhrases(mcsLeadPlayer))
                    {
                        mcsBotPlayer.AIData.BotOwner.BotTalk.TrySay(msg.PhraseTrigger);
                    }
                    ShowMsg(mcsLeadPlayer, mcsBotPlayer, msg);
                }
                else
                {
                    if (!ShouldShowSubtitles(mcsLeadPlayer.ProfileId))
                    {
                        return;
                    }

                    EventMgr.Notify(new SubtitlesMgrHandleFikaEvent
                    {
                        McsLeadPlayerId = mcsLeadPlayer.ProfileId,
                        McsBotPlayerId = mcsBotPlayer.ProfileId,
                        Msg = msg
                    });
                }
            }
            else
            {
                if (!ShouldSilencePhrases(mcsLeadPlayer))
                {
                    mcsBotPlayer.AIData.BotOwner.BotTalk.TrySay(msg.PhraseTrigger);
                }
                ShowMsg(mcsLeadPlayer, mcsBotPlayer, msg);
            }
        }

        private bool ShouldSilencePhrases(Player mcsLeadPlayer)
        {
            return ShouldSilencePhrases(mcsLeadPlayer.ProfileId);
        }

        public bool ShouldSilencePhrases(MongoID mcsLeadPlayerId)
        {
            if (McsMgr != null && McsMgr.McsLeadPlayerConfigs.TryGetValue(mcsLeadPlayerId, out var mcsBotPlayerConfig))
            {
                return mcsBotPlayerConfig.PhrasesSilent;
            }
            return MiyakoCarryServicePlugin.PhrasesSilent.Value;
        }

        public bool ShouldShowSubtitles(MongoID mcsLeadPlayerId)
        {
            if (McsMgr != null && McsMgr.McsLeadPlayerConfigs.TryGetValue(mcsLeadPlayerId, out var mcsBotPlayerConfig))
            {
                return mcsBotPlayerConfig.EnableSubtitles;
            }
            return true;
        }

        public bool ShouldSilenceBot(Player mcsBotPlayer)
        {
            if (!Tools.IsHost)
            {
                return false;
            }

            if (mcsBotPlayer == null)
            {
                return false;
            }

            if (McsMgr == null || !McsMgr.IsMcsBotPlayer(mcsBotPlayer.ProfileId))
            {
                return false;
            }

            var mcsLeadPlayer = McsMgr.GetMcsLeadPlayerByMcsBotPlayerId(mcsBotPlayer.ProfileId);
            if (mcsLeadPlayer == null)
            {
                return false;
            }

            return ShouldSilencePhrases(mcsLeadPlayer.ProfileId);
        }

        public string HandleOnFirstContact(string content, McsMsg msg, Player mcsLeadPlayer, Player mcsBotPlayer)
        {
            var toEnemy = msg.Position.Value - mcsLeadPlayer.Position;
            var flatToEnemy = new Vector3(toEnemy.x, 0, toEnemy.z);
            var flatDirection = new Vector3(mcsLeadPlayer.InteractionRay.direction.x, 0, mcsLeadPlayer.InteractionRay.direction.z);

            flatToEnemy.Normalize();
            flatDirection.Normalize();

            var dot = Vector3.Dot(flatDirection, flatToEnemy);
            var cross = Vector3.Cross(flatDirection, flatToEnemy);
            var angleThreshold = 0.707f;
            if (dot >= angleThreshold)
            {
                content += Locales.INTHEFRONT.McsLocalized();
            }
            else if (dot <= -angleThreshold)
            {
                content += Locales.ONSIX.McsLocalized();
            }
            else
            {
                if (cross.y > 0)
                {
                    content += Locales.RIGHTFLANK.McsLocalized();
                }
                else
                {
                    content += Locales.LEFTFLANK.McsLocalized();
                }
            }
            return content;
        }

        public string HandleOnLoot(string content, McsMsg msg, Player mcsLeadPlayer, Player mcsBotPlayer)
        {
            content = string.Format(content, msg.Keys[0].McsLocalized(), msg.Keys[1].McsLocalized());
            return content;
        }

        public string HandleLootGeneric(string content, McsMsg msg, Player mcsLeadPlayer, Player mcsBotPlayer)
        {
            content = string.Format(content, msg.Keys[0].McsLocalized());
            return content;
        }

        public string HandlePhraseNone(string content, McsMsg msg, Player mcsLeadPlayer, Player mcsBotPlayer)
        {
            var sb = new StringBuilder();
            foreach (var key in msg.Keys)
            {
                sb.Append(key.McsLocalized());
            }
            return sb.ToString();
        }

        public string HandlePhraseMine(string content, McsMsg msg, Player mcsLeadPlayer, Player mcsBotPlayer)
        {
            content = string.Format(content, msg.Keys[0], msg.Keys[1]);
            var healthStates = Json.Deserialize<List<HealthState>>(msg.Keys[2]);
            if (healthStates.Count > 0)
            {
                content += " " + string.Join(", ", healthStates.Select(h => $"{h.BodyPart.McsLocalized()} {h.EffectType.McsLocalized()}"));
            }
            return content;
        }

        public string HandlePhraseWithOnFight(string content, McsMsg msg, Player mcsLeadPlayer, Player mcsBotPlayer)
        {
            if (msg.Keys != null)
            {
                foreach (var key in msg.Keys)
                {
                    content += key.McsLocalized();
                }
            }
            return content;
        }

        public void ShowMsg(Player mcsLeadPlayer, Player mcsBotPlayer, McsMsg msg)
        {
            if (!MiyakoCarryServicePlugin.EnableSubtitles.Value)
            {
                return;
            }

            _talkContents.TryGetValue(msg.PhraseTrigger, out var talkContent);
            talkContent = talkContent.McsLocalized();
            if (string.IsNullOrEmpty(talkContent))
            {
                return;
            }

            if (_phraseHandleMaps.TryGetValue(msg.PhraseTrigger, out var action))
            {
                talkContent = action(talkContent, msg, mcsLeadPlayer, mcsBotPlayer);
            }

            if (!_subTitles.ContainsKey(mcsBotPlayer.Profile.Id))
            {
                CreateSubTitle(mcsBotPlayer.Profile);
            }

            if (_subTitles.TryGetValue(mcsBotPlayer.Profile.Id, out var subTitle))
            {
                if (subTitle.CurrentMsg() == talkContent)
                {
                    return;
                }

                subTitle.Show(talkContent, msg.PhraseTrigger);
            }
        }

        public void CreateSubTitle(Profile mcsBotPlayerProfile)
        {
            if (_subtitlesViewTemplate == null)
            {
                return;
            }
            var cloneSubtitleViewGameObject = Instantiate(_subtitlesViewTemplate);
            var cloneSubtitleView = cloneSubtitleViewGameObject.GetComponentInChildren<SubtitlesView>();

            cloneSubtitleViewGameObject.transform.SetParent(_subsContainer);

            var cloneSubTitle = new Subtitles(Gameloop, cloneSubtitleView, mcsBotPlayerProfile);
            cloneSubTitle.Hide(0f);
            _subTitles[mcsBotPlayerProfile.Id] = cloneSubTitle;
        }

        public override void OnRaidEnded()
        {
            base.OnRaidEnded();
            if (_subTitles != null)
            {
                foreach (var subTitle in _subTitles.Values)
                {
                    if (subTitle?.SubtitlesView?.gameObject == null)
                    {
                        continue;
                    }
                    Destroy(subTitle.SubtitlesView.gameObject);
                }
                _subTitles.Clear();
            }
        }

        public class Subtitles
        {
            public SubtitlesView SubtitlesView;
            private TMP_Text _textField;
            private GameLoop _gameLoop;
            private Coroutine _coroutine;
            private EPhraseTrigger _lastPhraseTrigger;
            private Profile _mcsBotPlayerProfile;
            private float _cooddown;

            public Subtitles(GameLoop gameLoop, SubtitlesView subtitlesView, Profile mcsBotPlayerProfile)
            {
                _lastPhraseTrigger = EPhraseTrigger.None;
                _mcsBotPlayerProfile = mcsBotPlayerProfile;
                _cooddown = 0;
                SubtitlesView = subtitlesView;
                _gameLoop = gameLoop;
                var subtitlesViewTraverse = Traverse.Create(subtitlesView);
                _textField = subtitlesViewTraverse.Field<TMP_Text>("_textField").Value;
            }

            public void Show(string msg, EPhraseTrigger talkContentType)
            {
                if (_lastPhraseTrigger == talkContentType)
                {
                    if (Time.time < _cooddown)
                    {
                        return;
                    }
                }

                Hide(0f);
                if (_coroutine != null)
                {
                    _gameLoop.StopCoroutine(_coroutine);
                }
                _textField.text = $"<b>{_mcsBotPlayerProfile.McsNickname}</b>: " + msg;
                if (SubtitlesView != null)
                {
                    SubtitlesView.ShowGameObject();
                    _cooddown = Time.time + 2f;
                    _lastPhraseTrigger = talkContentType;
                    _coroutine = _gameLoop.StartCoroutine(Hide(4f));
                }
            }

            public string CurrentMsg()
            {
                return _textField.text;
            }

            public IEnumerator Hide(float time)
            {
                yield return new WaitForSeconds(time);
                if (SubtitlesView != null)
                {
                    SubtitlesView.HideGameObject();
                    _textField.text = string.Empty;
                }
            }
        }
    }
}