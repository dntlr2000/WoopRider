using System.Collections.Generic;
using UnityEngine;

public static class EquipmentCatalog
{
    private const string EquipmentResourcesPath = "Equipment";

    private static Dictionary<string, EquipmentDefinition> definitionsById;
    private static List<EquipmentDefinition> definitions;

    public static EquipmentDefinition Get(string equipmentId)
    {
        // Resolve an equipment definition by id from the cached Resources catalog.
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(equipmentId))
        {
            return null;
        }

        definitionsById.TryGetValue(equipmentId, out EquipmentDefinition definition);
        return definition;
    }

    public static bool TryGet(string equipmentId, out EquipmentDefinition definition)
    {
        // Provide a bool-returning lookup for guard paths.
        definition = Get(equipmentId);
        return definition != null;
    }

    public static IReadOnlyList<EquipmentDefinition> GetAll()
    {
        // Return the loaded equipment definitions so spawning systems can choose drop candidates.
        EnsureLoaded();
        return definitions;
    }

    public static EquipmentDefinition GetRandom()
    {
        // Choose a random equipment definition from Resources for temporary field drops.
        EnsureLoaded();
        if (definitions.Count == 0)
        {
            return null;
        }

        return definitions[Random.Range(0, definitions.Count)];
    }

    private static void EnsureLoaded()
    {
        // Load all Resources/Equipment definitions once and index them by stable equipment id.
        if (definitionsById != null)
        {
            return;
        }

        definitionsById = new Dictionary<string, EquipmentDefinition>();
        definitions = new List<EquipmentDefinition>();
        EquipmentDefinition[] loadedDefinitions = Resources.LoadAll<EquipmentDefinition>(EquipmentResourcesPath);
        for (int i = 0; i < loadedDefinitions.Length; i++)
        {
            EquipmentDefinition definition = loadedDefinitions[i];
            if (definition == null || string.IsNullOrWhiteSpace(definition.EquipmentId))
            {
                continue;
            }

            definitionsById[definition.EquipmentId] = definition;
            definitions.Add(definition);
        }
    }
}
