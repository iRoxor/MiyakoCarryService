
using System;
using System.Threading.Tasks;
using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Models;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.Bots.Brain.Logics
{
    public class GoToLootTargetLogic : McsBotBaseLogic
    {
        private GoToSomePoint _baseLogic;
        private int _currentLootingRetries = 0;
        private float _lastTimeCheckDistance = 0f;
        public GoToLootTargetLogic(BotOwner botOwner) : base(botOwner)
        {
            _baseLogic = new(botOwner);
        }

        public override void Start()
        {
            base.Start();
            var mcsBotPlayerData = BotOwner.GetMcsBotPlayerData();

            if (mcsBotPlayerData != null)
            {
                mcsBotPlayerData.IsLooting = true;
                BotOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.GoLoot
                });
            }
        }

        public override void Update(CustomLayer.ActionData data)
        {
            _baseLogic.UpdateNodeByMain(data);
            BotOwner.SetTargetMoveSpeed(1f);
            BotOwner.Sprint(true, false);
            BotOwner.SetPose(1f);
            BotOwner.Steering.LookToMovingDirection();
            var mcsBotPlayerData = BotOwner.GetMcsBotPlayerData();

            if (mcsBotPlayerData == null)
            {
                return;
            }

            if (!mcsBotPlayerData.IsLooting)
            {
                return;
            }

            if (mcsBotPlayerData.IsTaskRunning)
            {
                return;
            }

            if (_lastTimeCheckDistance < Time.time)
            {
                _currentLootingRetries++;
                if (_currentLootingRetries > 15)
                {
                    mcsBotPlayerData.IsLooting = false;
                    _currentLootingRetries = 0;
                    return;
                }

                _lastTimeCheckDistance = Time.time + 2f;

                if (!mcsBotPlayerData.LootingTarget.IsLocked())
                {
                    mcsBotPlayerData.IsLooting = false;
                    return;
                }

                var lootPos = mcsBotPlayerData.LootingTarget.RootTransform.position;
                var offset = BotOwner.Position - lootPos;
                var sqrDistance = BotOwner.Position.McsSqrDistance(lootPos);

#if DEBUG
                // McsLogger.LogWarning($"{mcsBotPlayerData.Player.Profile.McsNickname}, 目标: {mcsBotPlayerData.LootingTarget.Item.Name.McsLocalized()}, 价值: {mcsBotPlayerData.LootingTarget.Offer.Price}, 战利品坐标: {lootPos}, 自身坐标: {BotOwner.Position}, Sqr距离: {sqrDistance}, 高度差: {Math.Abs(offset.y)}");
#endif

                if (sqrDistance <= 9f && Math.Abs(offset.y) < 3f)
                {
                    BotOwner.SetTargetMoveSpeed(0f);
                    BotOwner.SetPose(0f);
                    BotOwner.Steering.LookToPoint(lootPos);
                    TasksExtensions.HandleExceptions(StartLooting());
                    return;
                }

                if (sqrDistance <= 100f)
                {
                    BotOwner.SetTargetMoveSpeed(1f);
                    BotOwner.Steering.LookToMovingDirection();
                    BotOwner.SetPose(1f);
                    BotOwner.Mover.Sprint(false);
                }
            }
        }

        private async Task StartLooting()
        {
            var mcsBotPlayerData = BotOwner.GetMcsBotPlayerData();
            try
            {
                if (mcsBotPlayerData == null)
                {
                    return;
                }

                if (mcsBotPlayerData.LootingTarget == null)
                {
                    return;
                }

                BotOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.OnLoot,
                    Keys = [mcsBotPlayerData.LootingTarget.Item.Name, $"{mcsBotPlayerData.LootingTarget.Offer.Price} {mcsBotPlayerData.LootingTarget.Offer.CurrencySignal}"]
                });

                mcsBotPlayerData.IsTaskRunning = true;
                await Task.Delay(1000);
                if (!mcsBotPlayerData.LootingTarget.IsLocked())
                {
                    return;
                }

                var player = BotOwner.GetPlayer;
                var inventoryController = player.InventoryController;
                var item = mcsBotPlayerData.LootingTarget?.Item;

                if (item == null || item.Parent == null)
                {
                    return;
                }

                var rootItem = item.GetRootItem();
                if (rootItem == null || rootItem.Owner == null)
                {
                    return;
                }

                var rootItemData = rootItem.GetData();
                if (rootItemData is PlayerData playerData && playerData.Player.HealthController.IsAlive)
                {
                    return;
                }

                if (!Singleton<GameWorld>.Instance.ItemOwners.TryGetValue(rootItem.Owner, out var itemOwner))
                {
                    return;
                }

                var lootableContainer = itemOwner.Transform.GetComponent<LootableContainer>();
                var isActuallyInContainer = lootableContainer != null;

                if (isActuallyInContainer)
                {
                    if (lootableContainer.DoorState == EDoorState.Shut || lootableContainer.DoorState == EDoorState.Locked)
                    {
                        var interactionResult = new InteractionResult(EInteractionType.Open);
                        player.CurrentManagedState.StartDoorInteraction(lootableContainer, interactionResult, null);

                        await Task.Delay(2500);

                        if (lootableContainer.DoorState < EDoorState.Open)
                        {
                            BotOwner.TalkMsg(new McsMsg
                            {
                                PhraseTrigger = EPhraseTrigger.PhraseNone,
                                Keys = [Locales.ONLOOTOPENCONTAINERFAILED]
                            });
                            return;
                        }
                    }

                    await Task.Delay(3000);
                }

                if (item.QuestItem)
                {
                    BotOwner.TalkMsg(new McsMsg
                    {
                        PhraseTrigger = EPhraseTrigger.PhraseNone,
                        Keys = [Locales.ONLOOTQUESTITEM]
                    });
                    return;
                }

                if (await HandleLootAction(mcsBotPlayerData, mcsBotPlayerData.LootingTarget))
                {
                    MiyakoCarryService.Client.C2Sain.LootLog.Result(mcsBotPlayerData, mcsBotPlayerData.LootingTarget, true); // Fork: diagnostic log
                    BotOwner.TalkMsg(new McsMsg
                    {
                        PhraseTrigger = EPhraseTrigger.LootGeneric,
                        Keys = [item.Name]
                    });
                }
                else
                {
                    MiyakoCarryService.Client.C2Sain.LootLog.Result(mcsBotPlayerData, mcsBotPlayerData.LootingTarget, false); // Fork: diagnostic log
                    BotOwner.TalkMsg(new McsMsg
                    {
                        PhraseTrigger = EPhraseTrigger.PhraseNone,
                        Keys = [Locales.ONLOOTNOSPACE]
                    });
                }

                if (isActuallyInContainer)
                {
                    await Task.Delay(2000);
                    var interactionResult2 = new InteractionResult(EInteractionType.Close);
                    lootableContainer.Interact(interactionResult2);
                }
            }
            catch (Exception e)
            {
                McsLogger.LogError(e);
            }
            finally
            {
                if (mcsBotPlayerData != null)
                {
                    var target = mcsBotPlayerData.LootingTarget;
                    if (target != null && target.LootProps.TryGetValue(mcsBotPlayerData.McsAILeadPlayer, out var lootProp))
                    {
                        lootProp.SetLootCooldown(BotOwner);
                    }
                    mcsBotPlayerData.IsLooting = false;
                    mcsBotPlayerData.IsTaskRunning = false;
                }
            }
        }
    }
}