using UnityEngine;
using UnityEngine.SceneManagement;

namespace Button
{
    public class ExitButton : MonoBehaviour
    {
        private void OnMouseUp()
        {
            SceneManager.LoadScene("LobbyScene");
        }
    }
}