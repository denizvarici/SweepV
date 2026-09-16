using SweepV.Core.Cleanup;

namespace SweepV.Core.Tests
{
    public class InspectionTests
    {
        [Fact]
        public void Inspect_MissingFolders_IsNotApplicable()
        {
            var target = new CleanupTarget
            {
                Id = "test",
                Name = "Test",
                Description = "Test",
                ResolveFolders = () => [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())]
            };

            Assert.False(new CleanupService().Inspect(target).IsApplicable);
        }

        [Fact]
        public void Inspect_ExistingEmptyFolder_IsApplicableWithZeroBytes()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempPath);
            try
            {
                var target = new CleanupTarget { Id = "t", Name = "T", Description = "T", ResolveFolders = () => [tempPath] };

                var inspection = new CleanupService().Inspect(target);

                Assert.True(inspection.IsApplicable);
                Assert.Equal(0, inspection.Bytes);
            }
            finally
            {
                Directory.Delete(tempPath);
            }
        }

        [Fact]
        public void Inspect_FilePatternWithoutMatches_IsNotApplicable()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempPath);
            try
            {
                var target = new CleanupTarget { Id = "t", Name = "T", Description = "T", ResolveFolders = () => [tempPath], FilePattern = "MEMORY.DMP" };
                Assert.False(new CleanupService().Inspect(target).IsApplicable);
            }
            finally
            {
                Directory.Delete(tempPath);
            }
        }

        [Fact]
        public void Inspect_CommandThatThrows_StaysVisibleWithNote()
        {
            var target = new CleanupTarget
            {
                Id = "t",
                Name = "T",
                Description = "T",
                Kind = CleanupKind.Command,
                Inspect = _ => throw new InvalidOperationException("nope")
            };

            var inspection = new CleanupService().Inspect(target);

            Assert.True(inspection.IsApplicable);
            Assert.False(inspection.IsExact);
            Assert.Contains("nope", inspection.Note);
        }

        [Fact]
        public void Toggles_AreSeparateFromOneWayActions_AndQueryWithoutThrowing()
        {
            var toggles = SystemToggles.Create();
            var catalogIds = CleanupCatalog.CreateDefault().Select(t => t.Id).ToHashSet();

            Assert.Contains(toggles, t => t.Id == "hibernation");
            Assert.All(toggles, t => Assert.DoesNotContain(t.Id, catalogIds));
            foreach (var toggle in toggles.Where(t => t.IsSupported()))
                toggle.Query(CancellationToken.None);
        }

        [Fact]
        public void DefaultCatalog_InspectsWithoutThrowing_AndHasUniqueIds()
        {
            var catalog = CleanupCatalog.CreateDefault();
            Assert.Equal(catalog.Count, catalog.Select(t => t.Id).Distinct().Count());

            // Folder targets are covered above; measuring every real folder on the machine would be slow.
            var service = new CleanupService();
            foreach (var target in catalog.Where(t => t.Kind == CleanupKind.Command))
                service.Inspect(target);
            foreach (var target in catalog)
                _ = target.ResolveFolders().ToList();
        }
    }
}
