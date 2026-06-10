using System.Collections.Generic;
using UnityEngine;

public static class EquipmentCatalog
{
    private const string EquipmentResourcesPath = "Equipment";

    private static Dictionary<string, EquipmentDefinition> definitionsById;

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

    private static void EnsureLoaded()
    {
        // Load all Resources/Equipment definitions once and index them by stable equipment id.
        if (definitionsById != null)
        {
            return;
        }

        definitionsById = new Dictionary<string, EquipmentDefinition>();
        EquipmentDefinition[] definitions = Resources.LoadAll<EquipmentDefinition>(EquipmentResourcesPath);
        for (int i = 0; i < definitions.Length; i++)
        {
            EquipmentDefinition definition = definitions[i];
            if (definition == null || string.IsNullOrWhiteSpace(definition.EquipmentId))
            {
                continue;
            }

            definitionsById[definition.EquipmentId] = definition;
        }
    }
}
