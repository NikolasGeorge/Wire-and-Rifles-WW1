using FishNet;
using FishNet.Managing;
using UnityEngine;

public class ConnectionHud : MonoBehaviour
{
    // Explicitly stop connections when play mode tears down. If the editor
    // destroys the NetworkManager while Tugboat is still serving, the socket
    // thread can orphan and keep the port bound until the editor restarts.
    private void OnDestroy()
    {
        NetworkManager networkManager = InstanceFinder.NetworkManager;

        if (networkManager == null)
        {
            return;
        }

        if (networkManager.ClientManager.Started)
        {
            networkManager.ClientManager.StopConnection();
        }

        if (networkManager.ServerManager.Started)
        {
            networkManager.ServerManager.StopConnection(true);
        }
    }

    private void OnGUI()
    {
        NetworkManager networkManager = InstanceFinder.NetworkManager;

        if (networkManager == null)
        {
            return;
        }

        bool serverStarted = networkManager.ServerManager.Started;
        bool clientStarted = networkManager.ClientManager.Started;

        GuiScale.Begin();
        GUILayout.BeginArea(new Rect(10f, 10f, 160f, 120f));

        if (!serverStarted && !clientStarted)
        {
            if (GUILayout.Button("Host", GUILayout.Height(35f)))
            {
                networkManager.ServerManager.StartConnection();
                networkManager.ClientManager.StartConnection();
            }

            if (GUILayout.Button("Client", GUILayout.Height(35f)))
            {
                networkManager.ClientManager.StartConnection();
            }
        }
        else
        {
            if (GUILayout.Button("Disconnect", GUILayout.Height(35f)))
            {
                if (clientStarted)
                {
                    networkManager.ClientManager.StopConnection();
                }

                if (serverStarted)
                {
                    networkManager.ServerManager.StopConnection(true);
                }
            }
        }

        GUILayout.EndArea();
    }
}
