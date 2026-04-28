using System.Collections;
using UnityEngine;

public partial class RunawayObject
{
    private void BeginDestinationTaunt()
    {
        if (destinationTauntDuration <= 0f)
            return;

        tauntPlayedForCurrentDestination = true;
        tauntUntil = Time.time + destinationTauntDuration;
        agent.isStopped = true;
        PlayTauntStartSound();
        LogDebug(
            $"Entering destination taunt for {destinationTauntDuration:0.##}s at {FormatVector(agent.nextPosition)}."
        );
    }

    private bool IsTaunting()
    {
        return Time.time < tauntUntil;
    }

    private void PlayRunStartEffects()
    {
        PlaySound(runStartSound);

        if (runStartParticles == null)
            return;

        StopRunStartParticlesRoutine();
        runStartParticlesCoroutine = StartCoroutine(PlayDetachedRunStartParticles());
    }

    private void PlayTauntStartSound()
    {
        PlaySound(tauntStartSound);
    }

    private void PlaySound(AudioClip clip)
    {
        if (clip == null)
            return;

        if (Time.time < nextSoundAllowedTime)
            return;

        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();

        audioSource.PlayOneShot(clip);
        nextSoundAllowedTime = Time.time + soundCooldown;
    }

    private IEnumerator PlayDetachedRunStartParticles()
    {
        Transform particlesTransform = runStartParticles.transform;
        Vector3 worldPosition = particlesTransform.position;
        Quaternion worldRotation = particlesTransform.rotation;
        ParticleSystem.MainModule main = runStartParticles.main;

        runStartParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        particlesTransform.SetParent(null, true);
        particlesTransform.SetPositionAndRotation(worldPosition, worldRotation);
        runStartParticles.Play(true);

        float waitDuration = main.duration + main.startLifetime.constantMax + 0.1f;
        yield return new WaitForSeconds(waitDuration);

        runStartParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        particlesTransform.SetParent(runStartParticlesOriginalParent, false);
        particlesTransform.localPosition = runStartParticlesOriginalLocalPosition;
        particlesTransform.localRotation = runStartParticlesOriginalLocalRotation;
        particlesTransform.localScale = runStartParticlesOriginalLocalScale;
        main.simulationSpace = runStartParticlesOriginalSimulationSpace;
        runStartParticlesCoroutine = null;
    }

    private void StopRunStartParticlesRoutine()
    {
        if (runStartParticlesCoroutine != null)
        {
            StopCoroutine(runStartParticlesCoroutine);
            runStartParticlesCoroutine = null;
        }

        if (runStartParticles == null)
            return;

        runStartParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        Transform particlesTransform = runStartParticles.transform;
        if (particlesTransform.parent != runStartParticlesOriginalParent)
        {
            particlesTransform.SetParent(runStartParticlesOriginalParent, false);
            particlesTransform.localPosition = runStartParticlesOriginalLocalPosition;
            particlesTransform.localRotation = runStartParticlesOriginalLocalRotation;
            particlesTransform.localScale = runStartParticlesOriginalLocalScale;
        }

        ParticleSystem.MainModule main = runStartParticles.main;
        main.simulationSpace = runStartParticlesOriginalSimulationSpace;
    }
}
