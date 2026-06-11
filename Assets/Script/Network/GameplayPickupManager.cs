using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class GameplayPickupManager : NetworkBehaviour
{
    public static GameplayPickupManager Instance { get; private set; }

    private class PickupSlot
    {
        public bool Active;
        public bool IsFinalObjective;
        public PlayerStatType StatType;
        public Vector3 Position;
        public GameObject Visual;
        public Coroutine RespawnRoutine;
    }

    [Header("Main Match Pickups")]
    [SerializeField] private int statPickupCount = 12;
    [SerializeField] private float statRespawnDelay = 5f;

    [Header("Final Match Objective")]
    [SerializeField] private int finalObjectiveSlotIndex = 1000;

    [Header("Spawn Area")]
    [SerializeField] private Vector2 xRange = new(-18f, 18f);
    [SerializeField] private Vector2 zRange = new(-18f, 18f);
    [SerializeField] private float spawnY = 0.75f;

    [Header("Collection")]
    [SerializeField] private float collectRadius = 1.4f;
    [SerializeField] private float scanInterval = 0.1f;

    private readonly Dictionary<int, PickupSlot> slots = new();
    private PlayerStatsState statsState;
    private MatchStateController matchStateController;
    private float nextScanTime;
    private float nextLocalRequestTime;

    private void Awake()
    {
        // 아이템 매니저를 다른 게임플레이 스크립트에서 참조하기 위한 싱글턴 설정.
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        // 네트워크 스폰 후 상태 컨트롤러/스탯 상태를 연결하고 현재 경기 상태에 맞춰 초기화.
        statsState = PlayerStatsState.Instance;
        matchStateController = MatchStateController.Instance;

        CreateLocalVisualSlots();

        if (!IsServer)
        {
            return;
        }

        if (matchStateController != null && matchStateController.IsSpawned)
        {
            matchStateController.State.OnValueChanged += OnMatchStateChanged;
        }

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        }

        HandleStateEntered(matchStateController != null ? matchStateController.State.Value : NetworkMatchState.Lobby);
    }

    public override void OnNetworkDespawn()
    {
        // 서버 전용 이벤트 구독을 해제해 재시작/씬 정리 때 중복 호출을 방지.
        if (IsServer && matchStateController != null && matchStateController.IsSpawned)
        {
            matchStateController.State.OnValueChanged -= OnMatchStateChanged;
        }

        if (IsServer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
        }
    }

    private void Update()
    {
        // 서버는 서버 기준 위치로 자동 획득을 시도하고, 클라이언트는 자신의 위치 기준으로 획득 요청을 보낸다.
        if (IsServer && Time.time >= nextScanTime)
        {
            nextScanTime = Time.time + scanInterval;
            ScanPickupCollection();
        }

        if (IsClient && !IsServer && Time.time >= nextLocalRequestTime)
        {
            nextLocalRequestTime = Time.time + scanInterval;
            TryRequestLocalPickup();
        }
    }

    private void OnMatchStateChanged(NetworkMatchState previous, NetworkMatchState current)
    {
        // 경기 상태가 바뀔 때 해당 상태에 필요한 아이템 배치/정리를 수행.
        HandleStateEntered(current);
    }

    private void HandleStateEntered(NetworkMatchState state)
    {
        // 서버 기준으로 상태별 아이템/스탯 초기화 정책을 적용.
        if (!IsServer)
        {
            return;
        }

        switch (state)
        {
            case NetworkMatchState.Lobby:
                ClearAllPickups();
                statsState?.ResetStats();
                break;
            case NetworkMatchState.MatchMain:
                ClearAllPickups();
                NetworkPlayerEquipmentState.EquipDefaultForAll();
                NetworkPlayerCombatState.ResetForMatchStartForAll();
                statsState?.ResetStats();
                SpawnMainMatchPickups();
                break;
            case NetworkMatchState.FinalTransition:
                ClearAllPickups();
                break;
            case NetworkMatchState.FinalMatch:
                ClearAllPickups();
                List<ulong> restoredClientIds = NetworkPlayerEquipmentState.EquipDefaultForUnequippedAll();
                NetworkPlayerCombatState.ResetForClients(restoredClientIds);
                SpawnFinalObjective();
                break;
            case NetworkMatchState.Result:
                ClearAllPickups();
                break;
        }
    }

    private void SpawnMainMatchPickups()
    {
        // 메인 경기용 스탯 아이템 슬롯을 랜덤 위치와 랜덤 스탯으로 활성화.
        for (int i = 0; i < statPickupCount; i++)
        {
            ActivatePickup(i, GetRandomStatType(), GetRandomSpawnPosition(), false);
        }

        Debug.Log($"[GameplayPickupManager] Main match pickups spawned count={statPickupCount}");
    }

    private void SpawnFinalObjective()
    {
        // 최종전 승리 조건인 단일 목표 아이템을 필드에 배치.
        ActivatePickup(finalObjectiveSlotIndex, PlayerStatType.MoveSpeed, GetRandomSpawnPosition(), true);
        Debug.Log("[GameplayPickupManager] Final objective spawned.");
    }

    private void ActivatePickup(int slotId, PlayerStatType statType, Vector3 position, bool isFinalObjective)
    {
        // 서버 슬롯 상태를 활성화하고 모든 클라이언트에 비주얼 표시를 요청.
        PickupSlot slot = GetOrCreateSlot(slotId);
        slot.Active = true;
        slot.StatType = statType;
        slot.Position = position;
        slot.IsFinalObjective = isFinalObjective;

        SetPickupVisualClientRpc(slotId, true, position, statType, isFinalObjective);
    }

    private void DeactivatePickup(int slotId)
    {
        // 서버 슬롯 상태를 비활성화하고 모든 클라이언트에서 비주얼을 숨김.
        PickupSlot slot = GetOrCreateSlot(slotId);
        slot.Active = false;
        SetPickupVisualClientRpc(slotId, false, Vector3.zero, slot.StatType, slot.IsFinalObjective);
    }

    private void ClearAllPickups()
    {
        // 진행 중인 리스폰 예약을 취소하고 모든 아이템 슬롯을 비활성화.
        foreach (KeyValuePair<int, PickupSlot> pair in slots)
        {
            PickupSlot slot = pair.Value;
            if (slot.RespawnRoutine != null)
            {
                StopCoroutine(slot.RespawnRoutine);
                slot.RespawnRoutine = null;
            }

            slot.Active = false;
            SetPickupVisualClientRpc(pair.Key, false, Vector3.zero, slot.StatType, slot.IsFinalObjective);
        }
    }

    private void ScanPickupCollection()
    {
        // 서버가 네트워크 PlayerObject 위치 기준으로 아이템 획득 거리를 검사.
        if (NetworkManager.Singleton == null)
        {
            return;
        }

        foreach (KeyValuePair<int, PickupSlot> pair in slots)
        {
            PickupSlot slot = pair.Value;
            if (!slot.Active)
            {
                continue;
            }

            foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
            {
                if (!NetworkPlayerEquipmentState.ClientCanCollectItems(clientId))
                {
                    continue;
                }

                if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out NetworkClient client))
                {
                    continue;
                }

                NetworkObject playerObject = client.PlayerObject;
                if (playerObject == null)
                {
                    continue;
                }

                float sqrDistance = (playerObject.transform.position - slot.Position).sqrMagnitude;
                if (sqrDistance <= collectRadius * collectRadius)
                {
                    CollectPickup(pair.Key, clientId);
                    break;
                }
            }
        }
    }

    private void CollectPickup(int slotId, ulong clientId)
    {
        // 최종 목표/스탯 아이템 종류에 따라 승자 확정 또는 스탯 증가를 처리.
        PickupSlot slot = GetOrCreateSlot(slotId);
        if (!slot.Active)
        {
            return;
        }

        if (!NetworkPlayerEquipmentState.ClientCanCollectItems(clientId))
        {
            Debug.LogWarning($"[GameplayPickupManager] Pickup rejected because client has no collecting equipment clientId={clientId}");
            return;
        }

        if (slot.IsFinalObjective)
        {
            DeactivatePickup(slotId);
            matchStateController?.CompleteFinalObjectiveByClient(clientId);
            Debug.Log($"[GameplayPickupManager] Final objective collected clientId={clientId}");
            return;
        }

        statsState?.AddStat(clientId, slot.StatType, 1);
        DeactivatePickup(slotId);

        if (matchStateController != null && matchStateController.State.Value == NetworkMatchState.MatchMain)
        {
            slot.RespawnRoutine = StartCoroutine(RespawnStatPickupAfterDelay(slotId));
        }

        Debug.Log($"[GameplayPickupManager] Stat pickup collected clientId={clientId} stat={slot.StatType}");
    }

    private void TryRequestLocalPickup()
    {
        // 클라이언트 로컬 조작 캐릭터 위치 기준으로 가까운 아이템 획득을 서버에 요청.
        if (!LocalPlayerCanCollectItems())
        {
            return;
        }

        if (NetworkManager.Singleton == null || !TryGetLocalCollectionPosition(out Vector3 playerPosition))
        {
            return;
        }

        foreach (KeyValuePair<int, PickupSlot> pair in slots)
        {
            PickupSlot slot = pair.Value;
            if (!slot.Active)
            {
                continue;
            }

            float sqrDistance = (playerPosition - slot.Position).sqrMagnitude;
            if (sqrDistance <= collectRadius * collectRadius)
            {
                RequestPickupServerRpc(pair.Key, playerPosition);
                return;
            }
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestPickupServerRpc(int slotId, Vector3 reportedPlayerPosition, ServerRpcParams rpcParams = default)
    {
        // 클라이언트 요청은 서버가 슬롯 상태와 거리 조건을 다시 확인한 뒤 처리한다.
        PickupSlot slot = GetOrCreateSlot(slotId);
        if (!slot.Active)
        {
            return;
        }

        if (!NetworkPlayerEquipmentState.ClientCanCollectItems(rpcParams.Receive.SenderClientId))
        {
            Debug.LogWarning($"[GameplayPickupManager] Pickup request rejected because client has no collecting equipment clientId={rpcParams.Receive.SenderClientId}");
            return;
        }

        float maxAllowedDistance = collectRadius + 0.75f;
        float sqrDistance = (reportedPlayerPosition - slot.Position).sqrMagnitude;
        if (sqrDistance > maxAllowedDistance * maxAllowedDistance)
        {
            Debug.LogWarning($"[GameplayPickupManager] Pickup request rejected clientId={rpcParams.Receive.SenderClientId} slot={slotId} sqrDistance={sqrDistance:0.00}");
            return;
        }

        CollectPickup(slotId, rpcParams.Receive.SenderClientId);
    }

    private static bool LocalPlayerCanCollectItems()
    {
        // Let local clients skip pickup requests when their current equipment cannot collect.
        PlayerEquipment equipment = FindFirstObjectByType<PlayerEquipment>();
        return equipment != null && equipment.CanCollectItems;
    }

    private static bool TryGetLocalCollectionPosition(out Vector3 position)
    {
        // 현재 테스트 씬에서는 실제 조작 캐릭터인 ThirdPersonController 위치를 우선 사용한다.
        ThirdPersonController controller = FindFirstObjectByType<ThirdPersonController>();
        if (controller != null)
        {
            position = controller.transform.position;
            return true;
        }

        if (NetworkManager.Singleton != null &&
            NetworkManager.Singleton.SpawnManager != null &&
            NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject() != null)
        {
            position = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject().transform.position;
            return true;
        }

        position = default;
        return false;
    }

    private IEnumerator RespawnStatPickupAfterDelay(int slotId)
    {
        // 메인 경기 중 획득된 스탯 아이템을 일정 시간 뒤 새 위치에 재배치.
        yield return new WaitForSeconds(statRespawnDelay);

        if (!IsServer || matchStateController == null || matchStateController.State.Value != NetworkMatchState.MatchMain)
        {
            yield break;
        }

        PickupSlot slot = GetOrCreateSlot(slotId);
        slot.RespawnRoutine = null;
        ActivatePickup(slotId, GetRandomStatType(), GetRandomSpawnPosition(), false);
    }

    private void OnClientConnected(ulong clientId)
    {
        // 새 클라이언트가 접속하면 현재 활성 아이템 상태를 뒤늦게 동기화.
        StartCoroutine(SyncPickupsAfterClientJoin());
    }

    private IEnumerator SyncPickupsAfterClientJoin()
    {
        // 새 클라이언트가 씬 오브젝트를 스폰한 뒤 현재 아이템 상태를 다시 전파한다.
        yield return null;

        foreach (KeyValuePair<int, PickupSlot> pair in slots)
        {
            PickupSlot slot = pair.Value;
            SetPickupVisualClientRpc(pair.Key, slot.Active, slot.Position, slot.StatType, slot.IsFinalObjective);
        }
    }

    [ClientRpc]
    private void SetPickupVisualClientRpc(int slotId, bool active, Vector3 position, PlayerStatType statType, bool isFinalObjective)
    {
        // 서버 슬롯 상태를 받아 로컬 임시 비주얼의 표시/위치/색상을 갱신.
        PickupSlot slot = GetOrCreateSlot(slotId);
        slot.Active = active;
        slot.Position = position;
        slot.StatType = statType;
        slot.IsFinalObjective = isFinalObjective;

        EnsureVisual(slotId, slot);
        slot.Visual.SetActive(active);
        if (!active)
        {
            return;
        }

        slot.Visual.transform.position = position;
        Renderer renderer = slot.Visual.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material.color = GetPickupColor(statType, isFinalObjective);
        }
    }

    private void CreateLocalVisualSlots()
    {
        // 클라이언트별 임시 Primitive 비주얼을 미리 만들어 RPC 표시 요청에 대비.
        for (int i = 0; i < statPickupCount; i++)
        {
            EnsureVisual(i, GetOrCreateSlot(i));
        }

        PickupSlot finalSlot = GetOrCreateSlot(finalObjectiveSlotIndex);
        finalSlot.IsFinalObjective = true;
        EnsureVisual(finalObjectiveSlotIndex, finalSlot);
    }

    private void EnsureVisual(int slotId, PickupSlot slot)
    {
        // 슬롯에 아직 비주얼이 없으면 임시 Sphere/Capsule 오브젝트를 생성.
        if (slot.Visual != null)
        {
            return;
        }

        GameObject visual = GameObject.CreatePrimitive(slot.IsFinalObjective ? PrimitiveType.Capsule : PrimitiveType.Sphere);
        visual.name = $"PickupVisual_{slotId}";
        visual.transform.localScale = slot.IsFinalObjective ? new Vector3(1.2f, 1.8f, 1.2f) : Vector3.one;

        Collider collider = visual.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        visual.SetActive(false);
        slot.Visual = visual;
    }

    private PickupSlot GetOrCreateSlot(int slotId)
    {
        // 슬롯 딕셔너리에 없는 아이템 슬롯은 새로 생성해 반환.
        if (slots.TryGetValue(slotId, out PickupSlot slot))
        {
            return slot;
        }

        slot = new PickupSlot();
        slots.Add(slotId, slot);
        return slot;
    }

    private Vector3 GetRandomSpawnPosition()
    {
        // Inspector 범위 안에서 아이템 스폰 위치를 무작위로 생성.
        return new Vector3(
            Random.Range(xRange.x, xRange.y),
            spawnY,
            Random.Range(zRange.x, zRange.y));
    }

    private static PlayerStatType GetRandomStatType()
    {
        // 정의된 PlayerStatType 중 하나를 무작위로 선택.
        int typeCount = System.Enum.GetValues(typeof(PlayerStatType)).Length;
        return (PlayerStatType)Random.Range(0, typeCount);
    }

    private static Color GetPickupColor(PlayerStatType statType, bool isFinalObjective)
    {
        // 임시 Primitive 아이템에 적용할 스탯별 식별 색상을 반환.
        if (isFinalObjective)
        {
            return new Color(1f, 0.85f, 0.15f);
        }

        return statType switch
        {
            PlayerStatType.MoveSpeed => Color.cyan,
            PlayerStatType.JumpForce => Color.green,
            PlayerStatType.Weight => Color.gray,
            PlayerStatType.Health => Color.red,
            PlayerStatType.Defense => Color.blue,
            PlayerStatType.AttackPower => new Color(1f, 0.35f, 0.1f),
            PlayerStatType.FireRate => Color.magenta,
            _ => Color.white
        };
    }
}
