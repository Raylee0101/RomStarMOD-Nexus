using Candide;
using Candide.Entities.Controllers.Other;
using Candide.GameModels;
using Candide.GameModels.Managers;
using Candide.GameModels.Systems;
using Candide.Multiplayer.Network;
using Candide.Toolkit;
using Candide.World;
using CandideCreator.Shared.Models;
using CandideServer;
using CandideServer.Entities;
using CandideServer.Entities.Controllers;
using CandideServer.ServerManagers;
using CandideServer.ServerControllers;
using CandideServer.ServerSystems;
using CandideServer.SyncStrategies;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Shared.Combat;
using Shared.Entity;
using Shared.Entity.Components;
using Shared.Helpers;
using Shared.Models;

namespace RomStar.BepInEx.Features;

internal static class CartCapacityFeature
{
    private const string CartCapacityWorldFlag = "worship_flag:cart_capacity";
    private const int MinimumCartCapacity = 4;
    private const int MaximumCartCapacity = 50;
    private const int FirstExtraCartSlot = 6;

    private static bool cartCapacityOverrideEnabled;
    private static int cartCapacityValue = 10;
    private static int cartCapacityDraft = 10;
    private static int cartCapacityClientUpdateQueued;
    private static int cartCapacityServerUpdateQueued;
    private static bool cartCapacityClientWorldFlagSnapshotCaptured;
    private static bool cartCapacityServerWorldFlagSnapshotCaptured;
    private static bool cartCapacityServerHadWorldFlag;
    private static bool cartCapacityClientHadWorldFlag;
    private static int cartTakeQueued;
    private static WorldFlag cartCapacityServerOriginalWorldFlag;
    private static WorldFlag cartCapacityClientOriginalWorldFlag;
    private static DateTime lastCartCapacityTick = DateTime.MinValue;
    private static string status = "手推车容量模块已迁移到 BepInEx。";
    private static readonly Dictionary<Guid, DateTime> serverPickupSuppressUntil = new();
    private static readonly Dictionary<Guid, DateTime> recentServerExtraCartItems = new();
    private static readonly Dictionary<Guid, DateTime> autoStoreCooldownByCart = new();
    private static readonly Dictionary<Guid, Guid> virtualStoredCartIds = new();
    private static readonly List<Guid> virtualStoredEntityIds = new();
    private static int selectedVirtualStorageIndex;
    private static int virtualStorageActionQueued;
    private static int autoTakeQueued;
    private static DateTime lastAutoTakeUtc = DateTime.MinValue;

    public static void TickAlways()
    {
        if (!cartCapacityOverrideEnabled && !cartCapacityClientWorldFlagSnapshotCaptured && !cartCapacityServerWorldFlagSnapshotCaptured)
        {
            return;
        }

        DateTime utcNow = DateTime.UtcNow;
        double intervalMs = (cartCapacityOverrideEnabled || cartCapacityClientWorldFlagSnapshotCaptured || cartCapacityServerWorldFlagSnapshotCaptured) ? 100 : 1000;
        if ((utcNow - lastCartCapacityTick).TotalMilliseconds >= intervalMs)
        {
            lastCartCapacityTick = utcNow;
            ApplyCartCapacityOverrides();
        }
    }

    public static void Draw()
    {
        ImGui.TextWrapped($"Status: {status}");
        ImGui.Separator();
        ImGui.TextWrapped("Cart Capacity - experimental BepInEx port. Keep the old F1 trainer closed while testing this page.");
        ImGui.Checkbox("Enable cart capacity override", ref cartCapacityOverrideEnabled);
        ImGui.TextWrapped("Overrides vanilla 4/5 slots. Extra slots from slot 6 onward are managed by RomStar.");

        if (ImGui.InputInt("Target slots", ref cartCapacityDraft))
        {
            cartCapacityDraft = Math.Clamp(cartCapacityDraft, MinimumCartCapacity, MaximumCartCapacity);
        }

        if (ImGui.Button("10 slots"))
        {
            status = SetCartCapacityOverride(10);
        }
        ImGui.SameLine();
        if (ImGui.Button("20 slots"))
        {
            status = SetCartCapacityOverride(20);
        }
        ImGui.SameLine();
        if (ImGui.Button("50 slots"))
        {
            status = SetCartCapacityOverride(50);
        }

        if (ImGui.Button("Apply target slots"))
        {
            status = ApplyDraftCartCapacityOverride();
        }
        ImGui.SameLine();
        if (ImGui.Button("Restore vanilla"))
        {
            status = RestoreVanillaCartCapacity();
        }

        int value = Math.Clamp(cartCapacityValue, MinimumCartCapacity, MaximumCartCapacity);
        ImGui.TextWrapped(cartCapacityOverrideEnabled
            ? $"Storage mode active: vanilla cart stays at 5 physical slots; RomStar virtual target is {value} slots."
            : "Override inactive: vanilla cart capacity is 4 slots, or 5 after the Mercury worship reward.");

        ImGui.Separator();
        ImGui.TextWrapped("Cart Storage - automatic during normal play. Buttons below are emergency/debug controls.");
        ImGui.TextWrapped($"Stored items: {virtualStoredEntityIds.Count}");
        if (virtualStoredEntityIds.Count > 0)
        {
            selectedVirtualStorageIndex = Math.Clamp(selectedVirtualStorageIndex, 0, virtualStoredEntityIds.Count - 1);
            string[] labels = virtualStoredEntityIds.Select((id, index) => $"{index + 1}: {id.ToString()[..8]}").ToArray();
            ImGui.ListBox("Stored item", ref selectedVirtualStorageIndex, labels, labels.Length);
        }
        if (ImGui.Button("Store nearby item"))
        {
            QueueVirtualStorageAction(StoreNearbyItemOnServer);
        }
        ImGui.SameLine();
        if (ImGui.Button("Take selected item"))
        {
            QueueVirtualStorageAction(TakeSelectedItemOnServer);
        }
        if (ImGui.Button("Drop selected item"))
        {
            QueueVirtualStorageAction(DropSelectedItemOnServer);
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear virtual storage"))
        {
            QueueVirtualStorageAction(ClearVirtualStorageOnServer);
        }
    }

    public static void TryTakeNearestExtraCartItem()
    {
        if (!cartCapacityOverrideEnabled || Interlocked.Exchange(ref cartTakeQueued, 1) != 0)
        {
            return;
        }

        EntityWrapper? clientPlayer = Globals.Game?.Player;
        if (clientPlayer == null)
        {
            Volatile.Write(ref cartTakeQueued, 0);
            return;
        }

        Guid playerId = clientPlayer.Id;
        ServerDebugSystem.Queue((Action)delegate
        {
            try
            {
                TryTakeNearestExtraCartItemOnServer(playerId);
            }
            catch
            {
            }
            finally
            {
                Volatile.Write(ref cartTakeQueued, 0);
            }
        });
    }

    public static void TryAutoTakeFromNearestCart()
    {
        if (!cartCapacityOverrideEnabled || Interlocked.Exchange(ref autoTakeQueued, 1) != 0)
        {
            return;
        }

        EntityWrapper? clientPlayer = Globals.Game?.Player;
        if (clientPlayer == null)
        {
            Volatile.Write(ref autoTakeQueued, 0);
            return;
        }

        Guid playerId = clientPlayer.Id;
        ServerDebugSystem.Queue((Action)delegate
        {
            try
            {
                TryAutoTakeFromNearestCartOnServer(playerId);
            }
            catch (Exception ex)
            {
                status = "Auto cart take failed: " + ex.Message;
            }
            finally
            {
                Volatile.Write(ref autoTakeQueued, 0);
            }
        });
    }

    private static string SetCartCapacityOverride(int capacity)
    {
        cartCapacityDraft = Math.Clamp(capacity, MinimumCartCapacity, MaximumCartCapacity);
        cartCapacityValue = cartCapacityDraft;
        cartCapacityOverrideEnabled = true;
        ApplyCartCapacityOverrides();
        lastCartCapacityTick = DateTime.UtcNow;
        return $"Cart capacity override set to {cartCapacityValue} slots.";
    }

    private static string ApplyDraftCartCapacityOverride()
    {
        return SetCartCapacityOverride(cartCapacityDraft);
    }

    private static string RestoreVanillaCartCapacity()
    {
        cartCapacityOverrideEnabled = false;
        ApplyCartCapacityOverrides();
        lastCartCapacityTick = DateTime.UtcNow;
        return "Cart capacity restored to vanilla logic.";
    }

    private static void QueueVirtualStorageAction(Action<Guid> serverAction)
    {
        if (Interlocked.Exchange(ref virtualStorageActionQueued, 1) != 0)
        {
            return;
        }

        EntityWrapper? clientPlayer = Globals.Game?.Player;
        if (clientPlayer == null)
        {
            Volatile.Write(ref virtualStorageActionQueued, 0);
            return;
        }

        Guid playerId = clientPlayer.Id;
        ServerDebugSystem.Queue((Action)delegate
        {
            try
            {
                serverAction(playerId);
            }
            catch (Exception ex)
            {
                status = "Cart storage action failed: " + ex.Message;
            }
            finally
            {
                Volatile.Write(ref virtualStorageActionQueued, 0);
            }
        });
    }

    private static void StoreNearbyItemOnServer(Guid playerId)
    {
        if (!ServerGameState.Entities.TryGetValue(playerId, out ServerEntityModel? playerModel) || playerModel?.EntityWrapper == null)
        {
            return;
        }

        EntityWrapper player = playerModel.EntityWrapper;
        ServerEntityModel? best = ServerGameState.Entities.Values
            .Where(model => model.EntityWrapper != null && model.Id != playerId && !virtualStoredEntityIds.Contains(model.Id))
            .Where(model => model.EntityWrapper != null && ServerCart2Controller.CanBePickedUp(model.EntityWrapper))
            .OrderBy(model => Vector2.DistanceSquared(model.EntityWrapper!.Position2, player.Position2))
            .FirstOrDefault(model => Vector2.DistanceSquared(model.EntityWrapper!.Position2, player.Position2) <= 48f * 48f);

        if (best?.EntityWrapper == null)
        {
            status = "No nearby carriable item found.";
            return;
        }

        Guid? nearestCartId = FindNearestServerCart(player.Position2, 64f)?.Id;
        StoreEntityInVirtualStorage(best, nearestCartId);
        selectedVirtualStorageIndex = virtualStoredEntityIds.Count - 1;
        status = $"Stored item {best.Id.ToString()[..8]} in virtual cart storage.";
    }

    private static void TakeSelectedItemOnServer(Guid playerId)
    {
        if (!TryGetSelectedStoredEntity(out ServerEntityModel? target) ||
            !ServerGameState.Entities.TryGetValue(playerId, out ServerEntityModel? playerModel) ||
            playerModel?.EntityWrapper == null ||
            playerModel.CarriedEntityId.HasValue)
        {
            status = "Cannot take selected item right now.";
            return;
        }

        RemoveEntityFromVirtualStorage(target.Id);
        ShowStoredEntity(target, playerModel.EntityWrapper.Position + new Vector3(0f, 0f, 6f));
        target.CarrierEntityId = null;
        target.EntityWrapper!.CarrierId = null;
        if (EntityServerManager.TryAttach(playerModel, target, EntityAttachPriority.PlayerOrNpc, SyncStrategy.Everyone()))
        {
            status = $"Took item {target.Id.ToString()[..8]} from virtual storage.";
        }
        else
        {
            status = "Attach failed; item was restored near the player.";
        }
    }

    private static void DropSelectedItemOnServer(Guid playerId)
    {
        if (!TryGetSelectedStoredEntity(out ServerEntityModel? target) ||
            !ServerGameState.Entities.TryGetValue(playerId, out ServerEntityModel? playerModel) ||
            playerModel?.EntityWrapper == null)
        {
            status = "Cannot drop selected item right now.";
            return;
        }

        RemoveEntityFromVirtualStorage(target.Id);
        ShowStoredEntity(target, playerModel.EntityWrapper.Position + new Vector3(14f, 0f, 0f));
        status = $"Dropped item {target.Id.ToString()[..8]} from virtual storage.";
    }

    private static void ClearVirtualStorageOnServer(Guid playerId)
    {
        if (!ServerGameState.Entities.TryGetValue(playerId, out ServerEntityModel? playerModel) || playerModel?.EntityWrapper == null)
        {
            virtualStoredEntityIds.Clear();
            virtualStoredCartIds.Clear();
            return;
        }

        Vector3 dropBase = playerModel.EntityWrapper.Position;
        foreach (Guid id in virtualStoredEntityIds.ToList())
        {
            if (ServerGameState.Entities.TryGetValue(id, out ServerEntityModel? target) && target?.EntityWrapper != null)
            {
                ShowStoredEntity(target, dropBase + new Vector3(14f, 0f, 0f));
            }
        }
        virtualStoredEntityIds.Clear();
        virtualStoredCartIds.Clear();
        selectedVirtualStorageIndex = 0;
        status = "Cleared virtual cart storage.";
    }

    private static void StoreEntityInVirtualStorage(ServerEntityModel entity, Guid? cartId)
    {
        HideStoredEntity(entity);
        if (!virtualStoredEntityIds.Contains(entity.Id))
        {
            virtualStoredEntityIds.Add(entity.Id);
        }
        if (cartId.HasValue)
        {
            virtualStoredCartIds[entity.Id] = cartId.Value;
        }
    }

    private static void RemoveEntityFromVirtualStorage(Guid entityId)
    {
        virtualStoredEntityIds.Remove(entityId);
        virtualStoredCartIds.Remove(entityId);
        selectedVirtualStorageIndex = virtualStoredEntityIds.Count == 0
            ? 0
            : Math.Clamp(selectedVirtualStorageIndex, 0, virtualStoredEntityIds.Count - 1);
    }

    private static bool TryGetSelectedStoredEntity([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ServerEntityModel? entity)
    {
        entity = null;
        if (virtualStoredEntityIds.Count == 0)
        {
            return false;
        }

        selectedVirtualStorageIndex = Math.Clamp(selectedVirtualStorageIndex, 0, virtualStoredEntityIds.Count - 1);
        return ServerGameState.Entities.TryGetValue(virtualStoredEntityIds[selectedVirtualStorageIndex], out entity) && entity?.EntityWrapper != null;
    }

    private static void HideStoredEntity(ServerEntityModel entity)
    {
        entity.CarrierEntityId = null;
        entity.IsThrown = false;
        entity.ThrowerId = null;
        if (entity.EntityWrapper != null)
        {
            entity.EntityWrapper.CarrierId = null;
            entity.EntityWrapper.Visible = false;
            entity.EntityWrapper.Render3D = false;
            entity.EntityWrapper.NoEntityCollision = true;
            entity.EntityWrapper.NoTerrainCollision = true;
        }
        QueueClientEntityVisibility(entity.Id, visible: false, null);
    }

    private static void ShowStoredEntity(ServerEntityModel entity, Vector3 position)
    {
        entity.CarrierEntityId = null;
        entity.IsThrown = false;
        entity.ThrowerId = null;
        EntitySController.UpdateEntityPosition(entity, position);
        if (entity.EntityWrapper != null)
        {
            entity.EntityWrapper.CarrierId = null;
            entity.EntityWrapper.Visible = true;
            entity.EntityWrapper.Render3D = true;
            entity.EntityWrapper.NoEntityCollision = false;
            entity.EntityWrapper.NoTerrainCollision = false;
            entity.EntityWrapper.Position = position;
            entity.EntityWrapper.Velocity = Vector3.Zero;
            entity.EntityWrapper.System.CollisionGroup.UpdatePositionAndVelocity(entity.EntityWrapper);
        }
        QueueClientEntityVisibility(entity.Id, visible: true, position);
    }

    private static void QueueClientEntityVisibility(Guid entityId, bool visible, Vector3? position)
    {
        DebugSystem.Queue((Action)delegate
        {
            try
            {
                if (GameState.Entities != null && GameState.Entities.TryGetValue(entityId, out EntityWrapper? entity) && entity != null)
                {
                    entity.Visible = visible;
                    entity.Render3D = visible;
                    entity.NoEntityCollision = !visible;
                    entity.NoTerrainCollision = !visible;
                    if (position.HasValue)
                    {
                        entity.Position = position.Value;
                        entity.Velocity = Vector3.Zero;
                        entity.System.CollisionGroup.UpdatePositionAndVelocity(entity);
                    }
                }
            }
            catch
            {
            }
        });
    }

    private static int? GetCartCapacityOverride()
    {
        return cartCapacityOverrideEnabled ? Math.Clamp(cartCapacityValue, MinimumCartCapacity, MaximumCartCapacity) : null;
    }

    private static void ApplyCartCapacityOverrides()
    {
        QueueClientCartCapacityUpdate();
        if (LocalHostServerManager.StartedServer)
        {
            QueueServerCartCapacityUpdate();
        }
    }

    private static void QueueClientCartCapacityUpdate()
    {
        if (Interlocked.Exchange(ref cartCapacityClientUpdateQueued, 1) != 0)
        {
            return;
        }

        DebugSystem.Queue((Action)delegate
        {
            try
            {
                ApplyClientCartCapacityOverridesOnGameThread();
            }
            catch
            {
            }
            finally
            {
                Volatile.Write(ref cartCapacityClientUpdateQueued, 0);
            }
        });
    }

    private static void QueueServerCartCapacityUpdate()
    {
        if (Interlocked.Exchange(ref cartCapacityServerUpdateQueued, 1) != 0)
        {
            return;
        }

        ServerDebugSystem.Queue((Action)delegate
        {
            try
            {
                ApplyServerCartCapacityOverridesOnServerThread();
            }
            catch
            {
            }
            finally
            {
                Volatile.Write(ref cartCapacityServerUpdateQueued, 0);
            }
        });
    }

    private static void ApplyClientCartCapacityOverridesOnGameThread()
    {
        ApplyClientCartCapacityWorldFlagState();
        if (LocalHostServerManager.StartedServer)
        {
            ApplyClientCartCapacityOverride();
        }
    }

    private static void ApplyServerCartCapacityOverridesOnServerThread()
    {
        if (!LocalHostServerManager.StartedServer)
        {
            if (!cartCapacityOverrideEnabled)
            {
                RestoreServerCartCapacityWorldFlag();
            }
        }
        else
        {
            ApplyServerCartCapacityWorldFlagState();
            ApplyServerCartCapacityOverride();
        }
    }

    private static void ApplyClientCartCapacityWorldFlagState()
    {
        if (cartCapacityOverrideEnabled)
        {
            EnsureClientCartCapacityWorldFlagEnabled();
        }
        else
        {
            RestoreClientCartCapacityWorldFlag();
        }
    }

    private static void ApplyServerCartCapacityWorldFlagState()
    {
        if (cartCapacityOverrideEnabled)
        {
            EnsureServerCartCapacityWorldFlagEnabled();
        }
        else
        {
            RestoreServerCartCapacityWorldFlag();
        }
    }

    private static void EnsureClientCartCapacityWorldFlagEnabled()
    {
        if (!cartCapacityClientWorldFlagSnapshotCaptured)
        {
            cartCapacityClientHadWorldFlag = GameState.WorldFlags.TryGetValue(CartCapacityWorldFlag, out cartCapacityClientOriginalWorldFlag);
            cartCapacityClientWorldFlagSnapshotCaptured = true;
        }
        if (!GameState.WorldFlags.ContainsKey(CartCapacityWorldFlag))
        {
            GameState.WorldFlags[CartCapacityWorldFlag] = CreateCartCapacityWorldFlag();
        }
    }

    private static void EnsureServerCartCapacityWorldFlagEnabled()
    {
        if (!cartCapacityServerWorldFlagSnapshotCaptured)
        {
            cartCapacityServerHadWorldFlag = ServerGameState.WorldFlags.TryGetValue(CartCapacityWorldFlag, out cartCapacityServerOriginalWorldFlag);
            cartCapacityServerWorldFlagSnapshotCaptured = true;
        }
        if (!ServerGameState.WorldFlags.ContainsKey(CartCapacityWorldFlag))
        {
            ServerGameState.WorldFlags[CartCapacityWorldFlag] = CreateCartCapacityWorldFlag();
        }
    }

    private static void RestoreClientCartCapacityWorldFlag()
    {
        if (cartCapacityClientWorldFlagSnapshotCaptured)
        {
            if (cartCapacityClientHadWorldFlag)
            {
                GameState.WorldFlags[CartCapacityWorldFlag] = cartCapacityClientOriginalWorldFlag;
            }
            else
            {
                GameState.WorldFlags.Remove(CartCapacityWorldFlag);
            }
            cartCapacityClientWorldFlagSnapshotCaptured = false;
            cartCapacityClientHadWorldFlag = false;
            cartCapacityClientOriginalWorldFlag = default;
        }
    }

    private static void RestoreServerCartCapacityWorldFlag()
    {
        if (cartCapacityServerWorldFlagSnapshotCaptured)
        {
            if (cartCapacityServerHadWorldFlag)
            {
                ServerGameState.WorldFlags[CartCapacityWorldFlag] = cartCapacityServerOriginalWorldFlag;
            }
            else
            {
                ServerGameState.WorldFlags.Remove(CartCapacityWorldFlag);
            }
            cartCapacityServerWorldFlagSnapshotCaptured = false;
            cartCapacityServerHadWorldFlag = false;
            cartCapacityServerOriginalWorldFlag = default;
        }
    }

    private static WorldFlag CreateCartCapacityWorldFlag()
    {
        return new WorldFlag
        {
            IntValue1 = 1,
            FloatValue1 = 0f,
            IsStatic = false
        };
    }

    private static void ApplyServerCartCapacityOverride()
    {
        CleanupOrphanServerCarriedEntities();
        int? capacityOverride = GetCartCapacityOverride();
        if (!capacityOverride.HasValue)
        {
            return;
        }

        foreach (ServerEntityModel item in ServerGameState.Entities.Values.ToList())
        {
            if (item.EntityWrapper?.Controller is ServerCart2Controller cartController)
            {
                ApplyServerCartCapacityToCart(item, cartController, capacityOverride);
            }
        }
    }

    private static void TryTakeNearestExtraCartItemOnServer(Guid playerId)
    {
        int? capacityOverride = GetCartCapacityOverride();
        if (!capacityOverride.HasValue || !ServerGameState.Entities.TryGetValue(playerId, out ServerEntityModel? playerModel) || playerModel?.EntityWrapper == null)
        {
            return;
        }

        if (playerModel.CarriedEntityId.HasValue)
        {
            return;
        }

        EntityWrapper player = playerModel.EntityWrapper;
        ServerEntityModel? bestCartModel = null;
        ServerCart2Controller? bestCartController = null;
        ServerEntityModel? bestTarget = null;
        int bestSlot = 0;
        float bestDistance = float.MaxValue;

        foreach (ServerEntityModel cartModel in ServerGameState.Entities.Values.ToList())
        {
            if (cartModel.EntityWrapper?.Controller is not ServerCart2Controller cartController)
            {
                continue;
            }

            int highestSlot = Math.Min(GetHighestServerCartSlotIndex(cartModel, capacityOverride.Value), capacityOverride.Value);
            for (int slot = FirstExtraCartSlot; slot <= highestSlot; slot++)
            {
                Guid? carriedId = GetServerCartSlot(cartModel, cartController, slot);
                if (!carriedId.HasValue || !ServerGameState.Entities.TryGetValue(carriedId.Value, out ServerEntityModel? target) || target?.EntityWrapper == null)
                {
                    continue;
                }

                RememberServerExtraCartItem(target.Id);
                float distance = Vector2.DistanceSquared(target.EntityWrapper.Position2, player.Position2);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestCartModel = cartModel;
                    bestCartController = cartController;
                    bestTarget = target;
                    bestSlot = slot;
                }
            }
        }

        if (bestTarget == null)
        {
            foreach (Guid recentId in recentServerExtraCartItems.Keys.ToList())
            {
                if (!ServerGameState.Entities.TryGetValue(recentId, out ServerEntityModel? target) || target?.EntityWrapper == null)
                {
                    recentServerExtraCartItems.Remove(recentId);
                    continue;
                }

                if (target.CarrierEntityId.HasValue || !ServerCart2Controller.CanBePickedUp(target.EntityWrapper))
                {
                    continue;
                }

                float distance = Vector2.DistanceSquared(target.EntityWrapper.Position2, player.Position2);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestTarget = target;
                    bestCartModel = null;
                    bestCartController = null;
                    bestSlot = 0;
                }
            }
        }

        if (bestTarget?.EntityWrapper == null || bestDistance > 42f * 42f)
        {
            return;
        }

        EntityWrapper targetEntity = bestTarget.EntityWrapper;
        SuppressServerPickup(bestTarget.Id);
        recentServerExtraCartItems.Remove(bestTarget.Id);
        if (bestCartModel != null && bestCartController != null && bestSlot >= FirstExtraCartSlot)
        {
            SetServerCartSlot(bestCartModel, bestCartController, bestSlot, null);
        }

        bestTarget.CarrierEntityId = null;
        targetEntity.CarrierId = null;
        targetEntity.NoEntityCollision = false;
        targetEntity.NoTerrainCollision = false;
        targetEntity.IsThrown = false;
        targetEntity.ThrowerId = null;

        EntityServerManager.TryAttach(playerModel, bestTarget, EntityAttachPriority.PlayerOrNpc, SyncStrategy.Everyone());
    }

    private static void CleanupOrphanServerCarriedEntities()
    {
        foreach (ServerEntityModel item in ServerGameState.Entities.Values.ToList())
        {
            Guid? carrierId = item.EntityWrapper?.CarrierId ?? item.CarrierEntityId;
            if (carrierId.HasValue && !ServerGameState.Entities.ContainsKey(carrierId.Value))
            {
                if (item.EntityWrapper != null)
                {
                    item.EntityWrapper.CarrierId = null;
                    item.EntityWrapper.NoEntityCollision = false;
                    item.EntityWrapper.NoTerrainCollision = false;
                }
                item.CarrierEntityId = null;
                item.IsThrown = false;
                item.ThrowerId = null;
            }
        }
    }

    private static void ApplyServerCartCapacityToCart(ServerEntityModel cartModel, ServerCart2Controller cartController, int? capacityOverride)
    {
        int physicalCapacity = Math.Min(5, Math.Clamp(capacityOverride ?? GetVanillaServerCartCapacity(), MinimumCartCapacity, MaximumCartCapacity));
        int highestSlotIndex = GetHighestServerCartSlotIndex(cartModel, physicalCapacity);
        for (int i = physicalCapacity + 1; i <= highestSlotIndex; i++)
        {
            ReleaseServerCartSlot(cartModel, cartController, i);
        }
        if (capacityOverride.HasValue)
        {
            TryAutoStoreOverflowItem(cartModel, cartController, capacityOverride.Value);
        }
    }

    private static void TryAutoStoreOverflowItem(ServerEntityModel cartModel, ServerCart2Controller cartController, int targetCapacity)
    {
        int virtualCapacity = Math.Max(0, Math.Clamp(targetCapacity, MinimumCartCapacity, MaximumCartCapacity) - 5);
        if (virtualCapacity == 0 ||
            CountServerCartOccupiedSlots(cartModel, cartController, 5) < 5 ||
            CountVirtualItemsForCart(cartModel.Id) >= virtualCapacity ||
            IsAutoStoreOnCooldown(cartModel.Id))
        {
            return;
        }

        foreach (EntityWrapper candidate in GetServerCartPickupCandidates(cartController.Entity))
        {
            if (!TryGetAutoStoreCandidate(candidate, out ServerEntityModel? target))
            {
                continue;
            }

            StoreEntityInVirtualStorage(target, cartModel.Id);
            autoStoreCooldownByCart[cartModel.Id] = DateTime.UtcNow.AddMilliseconds(350);
            selectedVirtualStorageIndex = virtualStoredEntityIds.Count - 1;
            status = $"Auto-stored item {target.Id.ToString()[..8]} in cart storage.";
            return;
        }
    }

    private static bool TryGetAutoStoreCandidate(EntityWrapper candidate, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ServerEntityModel? target)
    {
        target = null;
        if (candidate.Controller is ServerCart2Controller ||
            candidate.CarrierId.HasValue ||
            IsServerPickupSuppressed(candidate.Id) ||
            virtualStoredEntityIds.Contains(candidate.Id) ||
            !ServerCart2Controller.CanBePickedUp(candidate))
        {
            return false;
        }

        if (!ServerGameState.Entities.TryGetValue(candidate.Id, out target) ||
            target?.EntityWrapper == null ||
            target.CarrierEntityId.HasValue)
        {
            target = null;
            return false;
        }

        return true;
    }

    private static bool IsAutoStoreOnCooldown(Guid cartId)
    {
        if (!autoStoreCooldownByCart.TryGetValue(cartId, out DateTime until))
        {
            return false;
        }
        if (until > DateTime.UtcNow)
        {
            return true;
        }
        autoStoreCooldownByCart.Remove(cartId);
        return false;
    }

    private static int CountVirtualItemsForCart(Guid cartId)
    {
        CleanupMissingVirtualStorageItems();
        return virtualStoredEntityIds.Count(id => virtualStoredCartIds.TryGetValue(id, out Guid storedCartId) && storedCartId == cartId);
    }

    private static void TryAutoTakeFromNearestCartOnServer(Guid playerId)
    {
        if (!cartCapacityOverrideEnabled ||
            (DateTime.UtcNow - lastAutoTakeUtc).TotalMilliseconds < 300 ||
            !ServerGameState.Entities.TryGetValue(playerId, out ServerEntityModel? playerModel) ||
            playerModel?.EntityWrapper == null ||
            playerModel.CarriedEntityId.HasValue)
        {
            return;
        }

        ServerEntityModel? cartModel = FindNearestServerCart(playerModel.EntityWrapper.Position2, 54f);
        if (cartModel?.EntityWrapper?.Controller is not ServerCart2Controller cartController ||
            CountServerCartOccupiedSlots(cartModel, cartController, 5) > 0)
        {
            return;
        }

        ServerEntityModel? target = FindVirtualStorageItemForCart(cartModel.Id);
        if (target?.EntityWrapper == null)
        {
            return;
        }

        RemoveEntityFromVirtualStorage(target.Id);
        ShowStoredEntity(target, playerModel.EntityWrapper.Position + new Vector3(0f, 0f, 6f));
        target.CarrierEntityId = null;
        target.EntityWrapper.CarrierId = null;
        lastAutoTakeUtc = DateTime.UtcNow;

        if (EntityServerManager.TryAttach(playerModel, target, EntityAttachPriority.PlayerOrNpc, SyncStrategy.Everyone()))
        {
            status = $"Auto-took item {target.Id.ToString()[..8]} from cart storage.";
        }
        else
        {
            status = "Auto take attach failed; item was restored near the player.";
        }
    }

    private static ServerEntityModel? FindNearestServerCart(Vector2 origin, float radius)
    {
        float maxDistance = radius * radius;
        return ServerGameState.Entities.Values
            .Where(model => model.EntityWrapper?.Controller is ServerCart2Controller)
            .OrderBy(model => Vector2.DistanceSquared(model.EntityWrapper!.Position2, origin))
            .FirstOrDefault(model => Vector2.DistanceSquared(model.EntityWrapper!.Position2, origin) <= maxDistance);
    }

    private static ServerEntityModel? FindVirtualStorageItemForCart(Guid cartId)
    {
        CleanupMissingVirtualStorageItems();
        Guid? entityId = virtualStoredEntityIds.FirstOrDefault(id => virtualStoredCartIds.TryGetValue(id, out Guid storedCartId) && storedCartId == cartId);
        if (!entityId.HasValue || entityId.Value == Guid.Empty)
        {
            entityId = virtualStoredEntityIds.FirstOrDefault(id => !virtualStoredCartIds.ContainsKey(id));
        }
        if (!entityId.HasValue || entityId.Value == Guid.Empty)
        {
            return null;
        }

        return ServerGameState.Entities.TryGetValue(entityId.Value, out ServerEntityModel? target) ? target : null;
    }

    private static void CleanupMissingVirtualStorageItems()
    {
        foreach (Guid id in virtualStoredEntityIds.Where(id => !ServerGameState.Entities.ContainsKey(id)).ToList())
        {
            RemoveEntityFromVirtualStorage(id);
        }
    }

    private static List<EntityWrapper> GetServerCartPickupCandidates(EntityWrapper cartEntity)
    {
        float radius = Math.Max(cartEntity.Shape.BoundingBox.Width / 2f - 5f + 18f, 28f);
        float minZ = cartEntity.Position.Z - 2f;
        float maxZ = cartEntity.Position.Z + 24f;
        return cartEntity.System.GetEntitiesTouchingCircleArea(cartEntity.Position2, radius, minZ, maxZ)
            .Where(entity => entity.Id != cartEntity.Id)
            .OrderBy(entity => Vector2.DistanceSquared(entity.Position2, cartEntity.Position2))
            .ToList();
    }

    private static int GetVanillaServerCartCapacity()
    {
        return WorldFlagsHelper.HasFlag(ServerGameState.WorldFlags, CartCapacityWorldFlag) ? 5 : 4;
    }

    private static int GetVanillaClientCartCapacity()
    {
        return WorldFlagsHelper.HasFlag(GameState.WorldFlags, CartCapacityWorldFlag) ? 5 : 4;
    }

    private static int GetHighestServerCartSlotIndex(ServerEntityModel cartModel, int desiredCapacity)
    {
        int highest = Math.Max(5, desiredCapacity);
        if (cartModel.Parameters == null)
        {
            return highest;
        }

        foreach (string key in cartModel.Parameters.Keys)
        {
            if (TryParseCartSlotKey(key, out int slot))
            {
                highest = Math.Max(highest, slot);
            }
        }
        return highest;
    }

    private static int CountServerCartOccupiedSlots(ServerEntityModel cartModel, ServerCart2Controller cartController, int capacity)
    {
        int count = 0;
        for (int i = 1; i <= capacity; i++)
        {
            if (GetServerCartSlot(cartModel, cartController, i).HasValue)
            {
                count++;
            }
        }
        return count;
    }

    private static Guid? GetServerCartSlot(ServerEntityModel cartModel, ServerCart2Controller cartController, int slot)
    {
        return slot switch
        {
            1 => cartController.Carried1,
            2 => cartController.Carried2,
            3 => cartController.Carried3,
            4 => cartController.Carried4,
            5 => cartController.Carried5,
            _ => GetGuidFromParameters(cartModel.Parameters, GetCartSlotKey(slot))
        };
    }

    private static bool TryAssignServerCartEntityToFirstFreeSlot(ServerEntityModel cartModel, ServerCart2Controller cartController, EntityWrapper carriedEntity, int capacity)
    {
        for (int i = 1; i <= capacity; i++)
        {
            if (GetServerCartSlot(cartModel, cartController, i).HasValue)
            {
                continue;
            }

            EntityWrapper cartEntity = cartController.Entity;
            SetServerCartSlot(cartModel, cartController, i, carriedEntity.Id);
            carriedEntity.IsThrown = false;
            carriedEntity.ThrowerId = null;
            carriedEntity.NoEntityCollision = true;
            carriedEntity.NoTerrainCollision = true;
            carriedEntity.CarrierId = cartEntity.Id;
            carriedEntity.Velocity = Vector3.Zero;
            carriedEntity.System.CollisionGroup.UpdatePositionAndVelocity(carriedEntity);

            if (ServerGameState.Entities.TryGetValue(carriedEntity.Id, out ServerEntityModel? value) && value != null)
            {
                value.CarrierEntityId = cartEntity.Id;
                value.IsThrown = false;
                value.ThrowerId = null;
            }
            if (i >= FirstExtraCartSlot)
            {
                RememberServerExtraCartItem(carriedEntity.Id);
                PositionServerExtraCartEntity(cartEntity, carriedEntity, i);
            }
            return true;
        }
        return false;
    }

    private static void MaintainServerExtraCartSlot(ServerEntityModel cartModel, ServerCart2Controller cartController, int slot)
    {
        Guid? carriedId = GetServerCartSlot(cartModel, cartController, slot);
        EntityWrapper cartEntity = cartController.Entity;
        if (carriedId.HasValue && ServerGameState.Entities.TryGetValue(carriedId.Value, out ServerEntityModel? value) && value?.EntityWrapper != null)
        {
            EntityWrapper carried = value.EntityWrapper;
            RememberServerExtraCartItem(carried.Id);
            if (carried.CarrierId.HasValue && carried.CarrierId != cartEntity.Id)
            {
                Guid newCarrierId = carried.CarrierId.Value;
                SuppressServerPickup(carried.Id);
                SetServerCartSlot(cartModel, cartController, slot, null);
                EnsureServerCarrierOwnsEntity(newCarrierId, value, carried);
                return;
            }
            carried.NoEntityCollision = true;
            carried.NoTerrainCollision = true;
            carried.IsThrown = false;
            carried.ThrowerId = null;
            carried.CarrierId = cartEntity.Id;
            value.CarrierEntityId = cartEntity.Id;
            value.IsThrown = false;
            value.ThrowerId = null;
            PositionServerExtraCartEntity(cartEntity, carried, slot);
        }
    }

    private static void ReleaseServerCartSlot(ServerEntityModel cartModel, ServerCart2Controller cartController, int slot)
    {
        Guid? carriedId = GetServerCartSlot(cartModel, cartController, slot);
        if (!carriedId.HasValue)
        {
            return;
        }

        if (ServerGameState.Entities.TryGetValue(carriedId.Value, out ServerEntityModel? value) && value != null)
        {
            value.CarrierEntityId = null;
            value.IsThrown = false;
            value.ThrowerId = null;
            if (value.EntityWrapper != null)
            {
                ReleaseServerCarriedEntity(cartController.Entity, value.EntityWrapper, slot);
            }
        }
        SetServerCartSlot(cartModel, cartController, slot, null);
    }

    private static void ReleaseServerCarriedEntity(EntityWrapper cartEntity, EntityWrapper carriedEntity, int slot)
    {
        if (carriedEntity.CarrierId == cartEntity.Id)
        {
            carriedEntity.CarrierId = null;
        }
        carriedEntity.NoEntityCollision = false;
        carriedEntity.NoTerrainCollision = false;
        carriedEntity.Position = cartEntity.Position + GetServerCartReleaseOffset(slot);
        carriedEntity.Velocity = Vector3.Zero;
        carriedEntity.System.CollisionGroup.UpdatePositionAndVelocity(carriedEntity);
    }

    private static Vector3 GetServerCartReleaseOffset(int slot)
    {
        int extraIndex = Math.Max(0, slot - 5);
        float x = 8f + extraIndex * 1.5f;
        float y = extraIndex % 2 == 0 ? 6f : -6f;
        return new Vector3(x, y, 6f);
    }

    private static void PositionServerExtraCartEntity(EntityWrapper cartEntity, EntityWrapper carriedEntity, int slot)
    {
        carriedEntity.Position = cartEntity.Position + new Vector3(0f, 0f, 6f);
        carriedEntity.Velocity = cartEntity.Velocity;
        carriedEntity.System.CollisionGroup.UpdatePositionAndVelocity(carriedEntity);
    }

    private static void SetServerCartSlot(ServerEntityModel cartModel, ServerCart2Controller cartController, int slot, Guid? carriedId)
    {
        if (GetServerCartSlot(cartModel, cartController, slot) == carriedId)
        {
            return;
        }

        switch (slot)
        {
            case 1:
                cartController.Carried1 = carriedId;
                break;
            case 2:
                cartController.Carried2 = carriedId;
                break;
            case 3:
                cartController.Carried3 = carriedId;
                break;
            case 4:
                cartController.Carried4 = carriedId;
                break;
            case 5:
                cartController.Carried5 = carriedId;
                break;
        }

        ServerEntitySystemManager.UpdateEntityParameter(cartController.Entity, GetCartSlotKey(slot), carriedId?.ToString() ?? string.Empty, SyncStrategy.Everyone(), true);
    }

    private static void SuppressServerPickup(Guid entityId)
    {
        serverPickupSuppressUntil[entityId] = DateTime.UtcNow.AddSeconds(3);
    }

    private static void RememberServerExtraCartItem(Guid entityId)
    {
        recentServerExtraCartItems[entityId] = DateTime.UtcNow.AddSeconds(8);
    }

    private static void EnsureServerCarrierOwnsEntity(Guid carrierId, ServerEntityModel targetModel, EntityWrapper targetEntity)
    {
        if (!ServerGameState.Entities.TryGetValue(carrierId, out ServerEntityModel? carrierModel) || carrierModel == null)
        {
            targetModel.CarrierEntityId = carrierId;
            return;
        }

        if (carrierModel.CarriedEntityId == targetModel.Id && targetModel.CarrierEntityId == carrierId)
        {
            return;
        }

        targetModel.CarrierEntityId = null;
        targetEntity.CarrierId = null;
        targetEntity.NoEntityCollision = false;
        targetEntity.NoTerrainCollision = false;

        if (!carrierModel.CarriedEntityId.HasValue &&
            EntityServerManager.TryAttach(carrierModel, targetModel, EntityAttachPriority.PlayerOrNpc, SyncStrategy.Everyone()))
        {
            targetEntity.CarrierId = carrierId;
            targetEntity.NoEntityCollision = true;
            targetEntity.NoTerrainCollision = true;
            targetEntity.IsThrown = false;
            targetEntity.ThrowerId = null;
            return;
        }

        targetModel.CarrierEntityId = carrierId;
        targetEntity.CarrierId = carrierId;
    }

    private static bool IsServerPickupSuppressed(Guid entityId)
    {
        return serverPickupSuppressUntil.TryGetValue(entityId, out DateTime until) && until > DateTime.UtcNow;
    }

    private static void CleanupServerPickupSuppression()
    {
        if (serverPickupSuppressUntil.Count == 0 && recentServerExtraCartItems.Count == 0 && autoStoreCooldownByCart.Count == 0)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        foreach (Guid entityId in serverPickupSuppressUntil.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList())
        {
            serverPickupSuppressUntil.Remove(entityId);
        }
        foreach (Guid entityId in recentServerExtraCartItems.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList())
        {
            recentServerExtraCartItems.Remove(entityId);
        }
        foreach (Guid cartId in autoStoreCooldownByCart.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList())
        {
            autoStoreCooldownByCart.Remove(cartId);
        }
    }

    private static void ApplyClientCartCapacityOverride()
    {
        if (GameState.Entities == null)
        {
            return;
        }

        int? capacityOverride = GetCartCapacityOverride();
        if (!capacityOverride.HasValue)
        {
            return;
        }

        foreach (EntityWrapper item in GameState.Entities.Values.ToList())
        {
            if (item.Controller is Cart2Controller cartController)
            {
                ApplyClientCartCapacityToCart(item, cartController, capacityOverride);
            }
        }
    }

    private static void ApplyClientCartCapacityToCart(EntityWrapper cartEntity, Cart2Controller cartController, int? capacityOverride)
    {
        int physicalCapacity = Math.Min(5, Math.Clamp(capacityOverride ?? GetVanillaClientCartCapacity(), MinimumCartCapacity, MaximumCartCapacity));
        int highestSlotIndex = GetHighestClientCartSlotIndex(cartController, physicalCapacity);
        for (int i = physicalCapacity + 1; i <= highestSlotIndex; i++)
        {
            ReleaseClientExtraCartSlot(cartEntity, cartController, i);
        }
    }

    private static int GetHighestClientCartSlotIndex(Cart2Controller cartController, int desiredCapacity)
    {
        int highest = Math.Max(5, desiredCapacity);
        foreach (string key in cartController.Parameters.Dictionary.Keys.ToList())
        {
            if (TryParseCartSlotKey(key, out int slot))
            {
                highest = Math.Max(highest, slot);
            }
        }
        return highest;
    }

    private static void MaintainClientExtraCartSlot(EntityWrapper cartEntity, Cart2Controller cartController, int slot)
    {
        Guid? carriedId = cartController.Parameters.GetGuidOrNull(GetCartSlotKey(slot), null);
        if (carriedId.HasValue && GameState.Entities.TryGetValue(carriedId.Value, out EntityWrapper? value) && value != null && (Globals.Game?.Player == null || value.CarrierId != Globals.Game.Player.Id))
        {
            bool noCollision = IsClientCartPickupDisabled(cartController);
            value.NoEntityCollision = noCollision;
            value.NoTerrainCollision = noCollision;
            value.IsThrown = false;
            value.ThrowerId = null;
            value.CarrierId = cartEntity.Id;
            value.Position = GetClientCartSlotWorldPosition(cartEntity, slot);
            value.Velocity = Vector3.Zero;
            value.System.CollisionGroup.UpdatePositionAndVelocity(value);
        }
    }

    private static bool IsClientCartPickupDisabled(Cart2Controller cartController)
    {
        EntityWrapper? player = Globals.Game?.Player;
        return player != null && cartController.FollowingId.HasValue && cartController.FollowingId.Value == player.Id;
    }

    private static void ReleaseClientExtraCartSlot(EntityWrapper cartEntity, Cart2Controller cartController, int slot)
    {
        Guid? carriedId = cartController.Parameters.GetGuidOrNull(GetCartSlotKey(slot), null);
        if (carriedId.HasValue && GameState.Entities.TryGetValue(carriedId.Value, out EntityWrapper? value) && value != null)
        {
            if (value.CarrierId == cartEntity.Id)
            {
                value.CarrierId = null;
            }
            value.NoEntityCollision = false;
            value.NoTerrainCollision = false;
        }
    }

    private static Vector3 GetClientCartSlotWorldPosition(EntityWrapper cartEntity, int slot)
    {
        int zeroBased = Math.Max(0, slot - FirstExtraCartSlot);
        int column = zeroBased % 4;
        int row = zeroBased / 4;
        float x = (column - 1.5f) * 4f;
        float y = 7f - row * 4.25f;
        float z = 6f + ((column + row) % 2 == 0 ? 0f : 0.5f);
        return TransformCartLocalPosition(cartEntity, x, y, z);
    }

    private static Vector3 TransformCartLocalPosition(EntityWrapper cartEntity, float x, float y, float z)
    {
        Vector3 transformed = Vector3.Transform(new Vector3(x, z, y), cartEntity.MeshTransformMatrixRef);
        return new Vector3(transformed.X, transformed.Z, transformed.Y) + cartEntity.Position + cartEntity.Velocity * cartEntity.Fdt;
    }

    private static string GetCartSlotKey(int slot)
    {
        return "c" + slot;
    }

    private static bool TryParseCartSlotKey(string key, out int slot)
    {
        slot = 0;
        return key.Length > 1 && key[0] == 'c' && int.TryParse(key[1..], out slot);
    }

    private static Guid? GetGuidFromParameters(Dictionary<string, string>? parameters, string key)
    {
        if (parameters == null || !parameters.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return Guid.TryParse(value, out Guid result) ? result : null;
    }
}
