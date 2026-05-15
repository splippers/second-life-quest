using System;
using SLQuest.Core;
using UnityEngine;

namespace SLQuest.Network
{
    public enum GridPreset { Agni, Aditi, Custom }

    [Serializable]
    public struct LoginCredentials
    {
        public string firstName;
        public string lastName;
        [NonSerialized] public string password;   // never serialized to disk
        public string startLocation;              // "last", "home", or "regionName/x/y/z"
        public GridPreset grid;
        public string customGridUri;
    }

    public sealed class LoginManager : MonoBehaviour
    {
        private SLNetworkManager _net;

        private void Awake()
        {
            _net = GetComponentInParent<SLApplication>()?.Network
                ?? FindObjectOfType<SLNetworkManager>();
        }

        public void Login(LoginCredentials creds)
        {
            if (_net == null)
            {
                Debug.LogError("[Login] SLNetworkManager not found");
                return;
            }

            string uri = creds.grid switch
            {
                GridPreset.Agni   => SLConstants.AGNI_LOGIN_URI,
                GridPreset.Aditi  => SLConstants.ADITI_LOGIN_URI,
                GridPreset.Custom => creds.customGridUri,
                _                 => SLConstants.AGNI_LOGIN_URI
            };

            Debug.Log($"[Login] Logging in as {creds.firstName} {creds.lastName} → {uri}");
            _net.BeginLogin(creds.firstName, creds.lastName, creds.password,
                            creds.startLocation, uri);
        }

        public void Logout() => _net?.Logout();

        /// <summary>
        /// Validates that credentials fields are non-empty without touching the grid.
        /// </summary>
        public static bool ValidateCredentials(LoginCredentials creds, out string error)
        {
            if (string.IsNullOrWhiteSpace(creds.firstName))  { error = "First name required"; return false; }
            if (string.IsNullOrWhiteSpace(creds.lastName))   { error = "Last name required";  return false; }
            if (string.IsNullOrWhiteSpace(creds.password))   { error = "Password required";   return false; }
            if (creds.grid == GridPreset.Custom && string.IsNullOrWhiteSpace(creds.customGridUri))
            { error = "Custom grid URI required"; return false; }
            error = null;
            return true;
        }
    }
}
