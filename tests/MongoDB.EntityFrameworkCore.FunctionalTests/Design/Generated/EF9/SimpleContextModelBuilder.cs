// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

#pragma warning disable 219, 612, 618
#nullable disable

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Design
{
    public partial class SimpleContextModel
    {
        private SimpleContextModel()
            : base(skipDetectChanges: false, modelId: new Guid("4359f75c-c779-4d1e-9887-8e0aeb0e5d92"), entityTypeCount: 2)
        {
        }

        partial void Initialize()
        {
            var everyType = EveryTypeEntityType.Create(this);
            var ownedEntity = OwnedEntityEntityType.Create(this);

            OwnedEntityEntityType.CreateForeignKey1(ownedEntity, everyType);

            EveryTypeEntityType.CreateAnnotations(everyType);
            OwnedEntityEntityType.CreateAnnotations(ownedEntity);

            AddAnnotation("ProductVersion", "9.0.3");
        }
    }
}
