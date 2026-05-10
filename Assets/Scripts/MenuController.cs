using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public class MenuController : MonoBehaviour
{
    [Header("Audio")]
    public AudioSource audioSource;
    public AudioClip startSFX;
    public float volume = 0.5f;

   public void StartBtn()
    {
        SceneManager.LoadScene("SampleScene");
        
        audioSource.PlayOneShot(startSFX, volume);
    }
}