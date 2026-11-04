
using System;
using System.Threading.Tasks;
using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using MiyakoCarryService.Client.Datas;
using MiyakoCarryService.Client.Events;
using MiyakoCarryService.Client.Extensions;
using MiyakoCarryService.Client.Mgrs;
using MiyakoCarryService.Client.Models;
using MiyakoCarryService.Client.Utils;
using UnityEngine;

namespace MiyakoCarryService.Client.Bots.Brain.Logics
{
    public class GoToExcuteProxyActionLogic : McsBotBaseLogic
    {
        private GoToSomePoint _baseLogic;
        private int _currentLootingRetries = 0;
        private float _lastTimeCheckDistance = 0f;
        public GoToExcuteProxyActionLogic(BotOwner botOwner) : base(botOwner)
        {
            _baseLogic = new(botOwner);
        }

        public override void Update(CustomLayer.ActionData data)
        {
            var mcsBotPlayerData = BotOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData == null)
            {
                BotOwner.Sprint(true, false);
                _baseLogic.UpdateNodeByMain(data);
                return;
            }

            var leadPlayer = mcsBotPlayerData.LeadPlayer;
            var botToLeaderSqrDistance = BotOwner.Position.McsSqrDistance(leadPlayer.Position);
            BotOwner.Steering.LookToMovingDirection();

            var leaderMovementContext = leadPlayer.MovementContext;
            var botWithin50 = botToLeaderSqrDistance < 50f * 50f;
            var leaderRelativeSpeed = leaderMovementContext.MaxSpeed > 0f ? leaderMovementContext.CharacterMovementSpeed / leaderMovementContext.MaxSpeed : 0f;

            // Fork: copy the player's pose only while following or escorting, never on a job of the bot's
            // own or in combat (upstream tested the game's enemy memory instead). C2Sain/MovePolicy.cs.
            var copyPose = C2Sain.MovePolicy.CopiesLeaderPose(BotOwner, mcsBotPlayerData, botWithin50);
            if (copyPose && leaderMovementContext.IsInPronePose)
            {
                _baseLogic.DoorOpen();
                BotOwner.BotLight?.TurnOff(false, true);
                BotOwner.GoToSomePointData.UpdateToGo(false, 0f, leaderMovementContext.PoseLevel);
            }
            else if (copyPose && (leaderMovementContext.PoseLevel < 1f || leaderMovementContext.PoseLevel == 1f && leaderRelativeSpeed < 1f))
            {
                _baseLogic.DoorOpen();
                BotOwner.BotLight?.TurnOff(false, true);
                BotOwner.GoToSomePointData.UpdateToGo(false, leaderMovementContext.CharacterMovementSpeed, leaderMovementContext.PoseLevel);
            }
            else
            {
                BotOwner.Sprint(true, false);
                _baseLogic.UpdateNodeByMain(data);
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

                var targetPos = mcsBotPlayerData.TargetPos;
                var offset = BotOwner.Position - targetPos.Value;
                var sqrDistance = BotOwner.Position.McsSqrDistance(targetPos.Value);

                if (sqrDistance <= 9f && Math.Abs(offset.y) < 3f)
                {
                    BotOwner.SetTargetMoveSpeed(0f);
                    BotOwner.Steering.LookToPoint(targetPos.Value);
                    TasksExtensions.HandleExceptions(StartExcuteProxyAction());
                    return;
                }

                if (sqrDistance <= 25f)
                {
                    BotOwner.SetTargetMoveSpeed(1f);
                    BotOwner.Steering.LookToMovingDirection();
                    BotOwner.Mover.Sprint(false);
                }
            }
        }

        private async Task StartExcuteProxyAction()
        {
            var mcsBotPlayerData = BotOwner.GetMcsBotPlayerData();
            try
            {
                if (mcsBotPlayerData == null)
                {
                    return;
                }

                BotOwner.TalkMsg(new McsMsg
                {
                    PhraseTrigger = EPhraseTrigger.OnPosition
                });

                mcsBotPlayerData.IsTaskRunning = true;
                if (mcsBotPlayerData.HasIntent(Intents.ShouldQuestProxyAction))
                {
                    BotOwner.SetPose(0f);
                    mcsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldHoldPosition);
                    await QuestProxyActionReadyToStart();
                }
                else if (mcsBotPlayerData.HasIntent(Intents.ShouldLootProxyAction))
                {
                    BotOwner.SetPose(0f);
                    mcsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldHoldPosition);
                    await StartLooting();
                }
                else if (mcsBotPlayerData.HasIntent(Intents.ShouldInteractionProxyAction))
                {
                    BotOwner.SetPose(1f);
                    mcsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldHoldPosition);
                    var interactableObjectData = Singleton<GameWorld>.Instance.FindInteractableObjectData(mcsBotPlayerData.ProxyTargetId);
                    if (interactableObjectData == null)
                    {
                        InteractionCallback(mcsBotPlayerData);
                        return;
                    }

                    var interactionResult = new InteractionResult(EInteractionType.Open);
                    if (interactableObjectData is DoorData doorData)
                    {
                        doorData.Door.DoorState = EDoorState.Shut;
                        mcsBotPlayerData.Player.StartInteraction(doorData.Door, interactionResult, () => InteractionCallback(mcsBotPlayerData));
                    }
                    else if (interactableObjectData is SwitchData switchData)
                    {
                        mcsBotPlayerData.Player.ExecuteInteraction(switchData.Switch, interactionResult);
                        await Task.Delay(1000);
                        InteractionCallback(mcsBotPlayerData);
                    }
                }
                else if (mcsBotPlayerData.HasIntent(Intents.ShouldStationaryWeaponProxyAction))
                {
                    var stationaryWeaponData = Singleton<GameWorld>.Instance.FindInteractableObjectData(mcsBotPlayerData.ProxyTargetId) as StationaryWeaponData;
                    if (stationaryWeaponData == null || stationaryWeaponData.StationaryWeapon == null)
                    {
                        InteractionCallback(mcsBotPlayerData);
                        return;
                    }

                    var stationary = BotOwner.WeaponManager.Stationary;
                    var stationaryWeaponLink = stationaryWeaponData.StationaryWeaponLink;
                    if (stationaryWeaponLink == null)
                    {
                        InteractionCallback(mcsBotPlayerData);
                        return;
                    }
                    else
                    {
                        stationary.SetTargetStationary(stationaryWeaponLink);
                    }
                    
                    mcsBotPlayerData.SetIntent([Intents.ShouldFollowMe, Intents.ShouldKeepFormation], Intents.ShouldUseStationaryWeapon, Intents.ShouldHoldPosition);
                    mcsBotPlayerData.TargetPos = null;
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
                    mcsBotPlayerData.IsTaskRunning = false;
                }
            }
        }

        private void InteractionCallback(McsBotPlayerData mcsBotPlayerData)
        {
            mcsBotPlayerData.RemoveIntent([Intents.ShouldInteractionProxyAction, Intents.ShouldQuestProxyAction, Intents.ShouldLootProxyAction, Intents.ShouldHoldPosition, Intents.ShouldUseStationaryWeapon, Intents.ShouldStationaryWeaponProxyAction]);
            mcsBotPlayerData.TargetPos = null;
            mcsBotPlayerData.ProxyTargetId = null;
        }

        private async Task QuestProxyActionReadyToStart()
        {
            var mcsBotPlayerData = BotOwner.GetMcsBotPlayerData();
            if (mcsBotPlayerData == null)
            {
                return;
            }
            var isMySquadMember = McsMgr.IsMcsMemberPlayer(mcsBotPlayerData.Player.ProfileId, out var mcsLeadPlayer);
            var mcsBotPlayer = mcsBotPlayerData.Player;
            if (mcsBotPlayer == null)
            {
                return;
            }

            if (MiyakoCarryServicePlugin.FikaInstalled && !isMySquadMember)
            {
                EventMgr.Notify(new QuestProxyCommandCallbackHandleFikaEvent
                {
                    McsLeadPlayerId = mcsBotPlayerData.LeadPlayer.ProfileId,
                    McsBotPlayerId = mcsBotPlayer.ProfileId,
                    TargetId = mcsBotPlayerData.ProxyTargetId
                });
            }
            else
            {
                var questData = QuestDataMgr.FindQuestData(mcsBotPlayerData.ProxyTargetId);
                if (questData != null)
                {
                    await questData.ForceCompleteQuest(mcsBotPlayer);
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
                    BotOwner.TalkMsg(new McsMsg
                    {
                        PhraseTrigger = EPhraseTrigger.LootGeneric,
                        Keys = [item.Name]
                    });
                }
                else
                {
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
                    mcsBotPlayerData.IsLooting = false;
                    mcsBotPlayerData.IsTaskRunning = false;
                    InteractionCallback(mcsBotPlayerData);
                }
            }
        }
    }
}