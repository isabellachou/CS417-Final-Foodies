using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public class PanController : MonoBehaviour
{
    [Header("Audio")]
    public AudioSource audioSource;
    public AudioClip butterSFX;
    public AudioClip eggSFX;
    public float volume=0.5f;

    [Header("Particles")]
    public ParticleSystem panParticles;

    [Header("Egg")]
    public GameObject rawEggPrefab;
    public Transform eggSpawnPoint;

    public void OnObjectPlaced(SelectEnterEventArgs args)
    {
        GameObject obj = args.interactableObject.transform.gameObject;

        if (obj.CompareTag("Egg"))
        {
            HandleEgg(obj);
        }
        else if (obj.CompareTag("Butter"))
        {
            HandleButter(obj);
        }
    }

    void HandleEgg(GameObject egg)
    {
        // Spawn raw egg in pan
        GameObject cookedEgg = Instantiate(
            rawEggPrefab, eggSpawnPoint.position, eggSpawnPoint.rotation);

        // Make it follow the pan
        cookedEgg.transform.SetParent(eggSpawnPoint);

        // Play sound & particles effect
        audioSource.PlayOneShot(eggSFX, volume);
        SpawnParticles();

        // Remove the original egg
        Destroy(egg);
    }

    void HandleButter(GameObject butter)
    {
        // Play sound & particles effect
        audioSource.PlayOneShot(butterSFX, volume);
        SpawnParticles();

        // Start shrinking
        StartCoroutine(ShrinkAndDestroy(butter));
    }

    public void ReleaseEggToPlate()
    {
        if (eggSpawnPoint.childCount > 0)
        {
            Transform egg = eggSpawnPoint.GetChild(0);
            egg.SetParent(null); // detach from pan
        }
    }

    private System.Collections.IEnumerator ShrinkAndDestroy(GameObject obj)
    {
        float duration = 3f;
        float time = 0f;

        Vector3 startScale = obj.transform.localScale;

        while (time < duration)
        {
            float t = time / duration;
            obj.transform.localScale = Vector3.Lerp(startScale, Vector3.zero, t);
            time += Time.deltaTime;
            yield return null;
        }

        Destroy(obj);
    }

    void SpawnParticles()
    {
        if (panParticles == null)
            return;

        panParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        panParticles.Play();
    }
}