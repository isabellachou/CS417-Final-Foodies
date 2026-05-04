using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using System.Collections.Generic;
using System.Collections;
using System;
using TMPro;

public enum RecipePhase { Selection, Ingredients, Cooking, Win }

[System.Serializable]
public class RecipeStep
{
    public string description;
    public string targetObjectName; // The object that needs to be interacted with
    public string requiredTagName;   // Optional: tag of the object to bring to target
    public bool isComplete;
}

[System.Serializable]
public class Recipe
{
    public string name;
    public List<string> ingredients;
    public List<RecipeStep> steps;
}

public class CookingTarget : MonoBehaviour
{
    public string targetName; // e.g., "Pot", "Pan", "Bowl"

    void OnCollisionEnter(Collision collision)
    {
        // For simplicity, we check if the object colliding is the one expected for the current step
        // OR we just notify the manager what object was dropped into this target
        string droppedName = collision.gameObject.name;
        RecipeManager.Instance.NotifyInteraction(targetName, droppedName);
    }
}

public class RecipeManager : MonoBehaviour
{
    public static RecipeManager Instance;

    public List<Recipe> recipes;
    public Recipe currentRecipe;
    public RecipePhase currentPhase = RecipePhase.Selection;
    public int currentStepIndex = 0;
    
    private List<string> collectedIngredients = new List<string>();

    public UnityEvent onPhaseChanged;
    public UnityEvent onIngredientCollected;
    public UnityEvent onStepCompleted;
    public UnityEvent onWin;

    public ParticleSystem stepParticles;
    public ParticleSystem winParticles;
    public AudioSource audioSource;
    public AudioClip chimeSound;
    public AudioClip victorySound;

    private List<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable> tools = new List<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    void Start()
    {
        // Find all tools and disable them
        string[] toolNames = { "Pot", "Pan", "Bowl", "PotLid", "Knife", "Whisk", "Ladle", "Spatula", "Plate", "Tray" };
        foreach (var name in toolNames)
        {
            GameObject obj = GameObject.Find(name);
            if (obj != null)
            {
                var interactable = obj.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
                if (interactable != null)
                {
                    tools.Add(interactable);
                    interactable.enabled = false;
                }
            }
        }
    }

    public void SelectRecipe(int index)
    {
        currentRecipe = recipes[index];
        currentPhase = RecipePhase.Ingredients;
        collectedIngredients.Clear();
        foreach (var step in currentRecipe.steps) step.isComplete = false;
        onPhaseChanged.Invoke();
    }

    public void CollectIngredient(string ingredientName)
    {
        if (currentPhase != RecipePhase.Ingredients) return;
        if (!currentRecipe.ingredients.Contains(ingredientName)) return;
        if (collectedIngredients.Contains(ingredientName)) return;

        collectedIngredients.Add(ingredientName);
        onIngredientCollected.Invoke();

        if (collectedIngredients.Count == currentRecipe.ingredients.Count)
        {
            StartCoroutine(TransitionToCooking());
        }
    }

    IEnumerator TransitionToCooking()
    {
        yield return new WaitForSeconds(1f);
        currentPhase = RecipePhase.Cooking;
        currentStepIndex = 0;
        onPhaseChanged.Invoke();
        
        // Enable tools
        foreach (var tool in tools)
        {
            if (tool != null) tool.enabled = true;
        }
    }

    public void NotifyInteraction(string targetName, string objectName)
    {
        if (currentPhase != RecipePhase.Cooking) return;
        if (currentStepIndex >= currentRecipe.steps.Count) return;

        var step = currentRecipe.steps[currentStepIndex];
        
        if (targetName == step.targetObjectName)
        {
            CompleteStep(step.description);
        }
    }

    public void CompleteStep(string stepName)
    {
        if (currentPhase != RecipePhase.Cooking) return;
        if (currentStepIndex >= currentRecipe.steps.Count) return;

        if (currentRecipe.steps[currentStepIndex].description == stepName)
        {
            currentRecipe.steps[currentStepIndex].isComplete = true;
            currentStepIndex++;
            
            if (stepParticles) stepParticles.Play();
            if (audioSource && chimeSound) audioSource.PlayOneShot(chimeSound);
            
            onStepCompleted.Invoke();

            if (currentStepIndex >= currentRecipe.steps.Count)
            {
                currentPhase = RecipePhase.Win;
                if (winParticles) winParticles.Play();
                if (audioSource && victorySound) audioSource.PlayOneShot(victorySound);
                onWin.Invoke();
            }
        }
    }

    public List<string> GetCollectedIngredients() => collectedIngredients;
}

public class UIManager : MonoBehaviour
{
    public RecipeManager recipeManager;
    
    public GameObject selectionPanel;
    public GameObject ingredientsPanel;
    public GameObject cookingPanel;
    public GameObject winPanel;

    public TextMeshProUGUI ingredientText;
    public TextMeshProUGUI cookingText;
    public TextMeshProUGUI winText;

    void Start()
    {
        recipeManager.onPhaseChanged.AddListener(UpdateUI);
        recipeManager.onIngredientCollected.AddListener(UpdateUI);
        recipeManager.onStepCompleted.AddListener(UpdateUI);
        recipeManager.onWin.AddListener(UpdateUI);
        UpdateUI();
    }

    void UpdateUI()
    {
        selectionPanel.SetActive(recipeManager.currentPhase == RecipePhase.Selection);
        ingredientsPanel.SetActive(recipeManager.currentPhase == RecipePhase.Ingredients);
        cookingPanel.SetActive(recipeManager.currentPhase == RecipePhase.Cooking);
        winPanel.SetActive(recipeManager.currentPhase == RecipePhase.Win);

        if (recipeManager.currentPhase == RecipePhase.Ingredients)
        {
            UpdateIngredientsList();
        }
        else if (recipeManager.currentPhase == RecipePhase.Cooking)
        {
            UpdateCookingSteps();
        }
        else if (recipeManager.currentPhase == RecipePhase.Win)
        {
            winText.text = $"🎉 Recipe Complete!\nYou made a {recipeManager.currentRecipe.name}!\n\nGreat job, Chef!";
        }
    }

    void UpdateIngredientsList()
    {
        string text = $"{recipeManager.currentRecipe.name}\nCollect your ingredients!\n\n";
        var collected = recipeManager.GetCollectedIngredients();
        foreach (var ingredient in recipeManager.currentRecipe.ingredients)
        {
            string check = collected.Contains(ingredient) ? "[✓]" : "[ ]";
            text += $"{check} {ingredient}\n";
        }
        text += $"\n{collected.Count}/{recipeManager.currentRecipe.ingredients.Count} collected";
        ingredientText.text = text;
    }

    void UpdateCookingSteps()
    {
        string text = $"{recipeManager.currentRecipe.name}\nStep {recipeManager.currentStepIndex + 1} of {recipeManager.currentRecipe.steps.Count}\n\n";
        for (int i = 0; i < recipeManager.currentRecipe.steps.Count; i++)
        {
            var step = recipeManager.currentRecipe.steps[i];
            string prefix = "";
            if (i < recipeManager.currentStepIndex) prefix = "[✓] ";
            else if (i == recipeManager.currentStepIndex) prefix = "[→] ";
            else prefix = "[ ] ";

            string line = prefix + step.description;
            if (i == recipeManager.currentStepIndex) line = $"<color=yellow>{line}</color>";
            
            text += line + "\n";
        }
        cookingText.text = text;
    }
}

[RequireComponent(typeof(UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable))]
public class RecipeIngredient : MonoBehaviour
{
    public string ingredientName;
    private bool hasBeenCollected = false;

    void Start()
    {
        var grab = GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
        grab.selectEntered.AddListener(OnGrabbed);
    }

    void OnGrabbed(UnityEngine.XR.Interaction.Toolkit.SelectEnterEventArgs args)
    {
        if (!hasBeenCollected)
        {
            RecipeManager.Instance.CollectIngredient(ingredientName);
            hasBeenCollected = true;
        }
    }
}
