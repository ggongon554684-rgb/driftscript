using UnityEngine;

public class BackgroundMusic : MonoBehaviour
{
    public AudioClip music;
    [Range(0f, 1f)] public float volume = 0.3f;

    void Start()
    {
        var s = gameObject.AddComponent<AudioSource>();
        s.clip = music;
        s.loop = true;
        s.volume = volume;
        s.spatialBlend = 0f;   // 2D sound
        s.Play();
    }
}