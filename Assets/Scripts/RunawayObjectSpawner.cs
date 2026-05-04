using System.Collections.Generic;
using UnityEngine;

public class RunawayObjectSpawner : MonoBehaviour
{
    [Header("Spawn Source")]
    [SerializeField]
    private RunawayObject eggPrefab;

    [SerializeField]
    private Transform spawnParent;

    [Header("Spawn Placement")]
    [SerializeField]
    private Transform[] spawnPoints;

    [SerializeField]
    private Vector3 fallbackSpawnOffset = Vector3.forward;

    [SerializeField]
    private bool chooseRandomSpawnPoint = true;

    [Header("Spawn Rules")]
    [SerializeField]
    [Min(1)]
    private int eggsPerPress = 1;

    [SerializeField]
    [Min(0)]
    private int maxActiveRunaways = 6;

    [SerializeField]
    private bool activateAfterSpawn = true;

    [SerializeField]
    private bool debugLogging = false;

    private readonly List<RunawayObject> activeRunaways = new List<RunawayObject>();
    private int nextSpawnPointIndex;

    private void OnDestroy()
    {
        for (int i = 0; i < activeRunaways.Count; i++)
        {
            if (activeRunaways[i] != null)
                activeRunaways[i].OnRunawayDespawned -= HandleRunawayDespawned;
        }

        activeRunaways.Clear();
    }

    [ContextMenu("Spawn Egg")]
    public void SpawnEgg()
    {
        LogDebug("SpawnEgg called.");
        SpawnEggs(eggsPerPress);
    }

    public void SpawnEggs(int count)
    {
        CleanupActiveRunaways();

        int spawnCount = Mathf.Max(0, count);
        for (int i = 0; i < spawnCount; i++)
        {
            if (IsAtActiveLimit())
            {
                LogDebug("Spawn skipped because max active runaway count has been reached.");
                return;
            }

            SpawnOneEgg();
        }
    }

    public RunawayObject SpawnOneEgg()
    {
        if (eggPrefab == null)
        {
            Debug.LogWarning("RunawayObjectSpawner needs an egg prefab before it can spawn.", this);
            return null;
        }

        GetSpawnPose(out Vector3 position, out Quaternion rotation);
        RunawayObject spawned = Instantiate(eggPrefab, position, rotation, spawnParent);
        RegisterRunaway(spawned);

        if (activateAfterSpawn)
            spawned.ActivateRunaway();

        LogDebug($"Spawned {spawned.name} at {position}.");
        return spawned;
    }

    private void RegisterRunaway(RunawayObject runaway)
    {
        if (runaway == null)
            return;

        activeRunaways.Add(runaway);
        runaway.OnRunawayDespawned += HandleRunawayDespawned;
    }

    private void HandleRunawayDespawned(RunawayObject runaway)
    {
        if (runaway != null)
            runaway.OnRunawayDespawned -= HandleRunawayDespawned;

        activeRunaways.Remove(runaway);
    }

    private bool IsAtActiveLimit()
    {
        return maxActiveRunaways > 0 && activeRunaways.Count >= maxActiveRunaways;
    }

    private void CleanupActiveRunaways()
    {
        for (int i = activeRunaways.Count - 1; i >= 0; i--)
        {
            if (activeRunaways[i] != null)
                continue;

            activeRunaways.RemoveAt(i);
        }
    }

    private void GetSpawnPose(out Vector3 position, out Quaternion rotation)
    {
        Transform spawnPoint = GetSpawnPoint();
        if (spawnPoint != null)
        {
            position = spawnPoint.position;
            rotation = spawnPoint.rotation;
            return;
        }

        position = transform.TransformPoint(fallbackSpawnOffset);
        rotation = transform.rotation;
    }

    private Transform GetSpawnPoint()
    {
        if (spawnPoints == null || spawnPoints.Length == 0)
            return null;

        if (chooseRandomSpawnPoint)
            return spawnPoints[Random.Range(0, spawnPoints.Length)];

        Transform spawnPoint = spawnPoints[nextSpawnPointIndex % spawnPoints.Length];
        nextSpawnPointIndex++;
        return spawnPoint;
    }

    private void LogDebug(string message)
    {
        if (!debugLogging)
            return;

        Debug.Log($"[RunawayObjectSpawner:{name}] {message}", this);
    }
}
