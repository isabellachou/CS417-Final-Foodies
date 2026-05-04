using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public class PlateController : MonoBehaviour
{
    [Header("Audio")]
    public AudioSource audioSource;
    public AudioClip plateSFX;
    public float volume = 0.5f;

    [Header("Particles")]
    public ParticleSystem plateParticles;

    public void OnObjectPlaced(SelectEnterEventArgs args)
    {
        GameObject obj = args.interactableObject.transform.gameObject;

        if (obj.CompareTag("CookedEgg"))
        {
            // Play sound
            audioSource.PlayOneShot(plateSFX, volume);

            // Play particles
            if (plateParticles != null)
            {
                plateParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                plateParticles.Play();
            }
        }
    }
}