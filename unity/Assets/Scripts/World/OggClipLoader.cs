using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace SLQuest.World
{
    /// <summary>
    /// Converts raw OGG bytes to an AudioClip by writing a temp file and using
    /// UnityWebRequestMultimedia to load it.  Call via coroutine.
    ///
    /// SL sound assets arrive as raw OGG Vorbis from Client.Assets.
    /// Unity's AudioClip.Create() cannot decode OGG in memory on all platforms,
    /// but UnityWebRequestMultimedia handles it via the platform audio decoder.
    /// </summary>
    public static class OggClipLoader
    {
        public static IEnumerator Load(byte[] oggData, string name, Action<AudioClip> callback)
        {
            if (oggData == null || oggData.Length == 0)
            {
                callback?.Invoke(null);
                yield break;
            }

            string path = Path.Combine(Application.temporaryCachePath, name + ".ogg");

            try { File.WriteAllBytes(path, oggData); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OggLoader] Failed to write temp file: {ex.Message}");
                callback?.Invoke(null);
                yield break;
            }

            string url = "file://" + path;
            using var req = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.OGGVORBIS);
            yield return req.SendWebRequest();

            AudioClip clip = null;
            if (req.result == UnityWebRequest.Result.Success)
                clip = DownloadHandlerAudioClip.GetContent(req);
            else
                Debug.LogWarning($"[OggLoader] {req.error} loading {name}");

            // Clean up temp file
            try { File.Delete(path); } catch { /* ignore */ }

            callback?.Invoke(clip);
        }
    }
}
