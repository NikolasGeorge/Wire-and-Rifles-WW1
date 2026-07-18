using TMPro;
using UnityEngine;

public class TeamTicketUI : MonoBehaviour
{
    public TeamTicketManager ticketManager;
    public PlayerTeam localPlayerTeam;

    [Header("Text")]
    public TMP_Text alliedPowersTicketText;
    public TMP_Text centralPowersTicketText;

    [Header("Colors")]
    public Color friendlyColor = new Color(0.1f, 0.45f, 1f, 1f);
    public Color enemyColor = new Color(1f, 0.15f, 0.1f, 1f);

    private void Awake()
    {
        if (ticketManager == null)
        {
            ticketManager = FindAnyObjectByType<TeamTicketManager>();
        }

        if (localPlayerTeam == null)
        {
            localPlayerTeam = FindLocalPlayerTeam();
        }
    }

    private void OnEnable()
    {
        TeamTicketManager.OnTicketsChanged += UpdateTicketUI;
        UpdateTicketUI();
    }

    private void OnDisable()
    {
        TeamTicketManager.OnTicketsChanged -= UpdateTicketUI;
    }

    private void Update()
    {
        UpdateTicketUI();
    }

    private void UpdateTicketUI()
    {
        if (ticketManager == null)
        {
            return;
        }

        if (alliedPowersTicketText != null)
        {
            alliedPowersTicketText.text = "Allied Powers: " + ticketManager.GetTickets(Team.AlliedPowers);
            alliedPowersTicketText.color = GetTeamColor(Team.AlliedPowers);
        }

        if (centralPowersTicketText != null)
        {
            centralPowersTicketText.text = "Central Powers: " + ticketManager.GetTickets(Team.CentralPowers);
            centralPowersTicketText.color = GetTeamColor(Team.CentralPowers);
        }
    }

    private Color GetTeamColor(Team team)
    {
        if (localPlayerTeam == null)
        {
            return Color.white;
        }

        return team == localPlayerTeam.team ? friendlyColor : enemyColor;
    }

    private PlayerTeam FindLocalPlayerTeam()
    {
        PlayerController playerController = FindAnyObjectByType<PlayerController>();

        if (playerController != null)
        {
            PlayerTeam playerTeam = playerController.GetComponentInParent<PlayerTeam>();

            if (playerTeam != null)
            {
                return playerTeam;
            }
        }

        GameObject playerObject = GameObject.FindGameObjectWithTag("Player");

        if (playerObject != null)
        {
            return playerObject.GetComponentInParent<PlayerTeam>();
        }

        return null;
    }
}