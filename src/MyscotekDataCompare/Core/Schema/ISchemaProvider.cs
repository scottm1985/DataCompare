namespace MyscotekDataCompare.Core.Schema
{
    /// <summary>Supplies entity metadata to the compare engine (so it is unit-testable without Dataverse).</summary>
    public interface ISchemaProvider { EntitySchema GetEntity(string logicalName); /* null if the entity does not exist */ }
}
