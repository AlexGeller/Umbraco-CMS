﻿using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AutoMapper;
using LightInject;
using Moq;
using NUnit.Framework;
using Umbraco.Core;
using Umbraco.Core.Cache;
using Umbraco.Core.Components;
using Umbraco.Core.DI;
using Umbraco.Core.Events;
using Umbraco.Core.IO;
using Umbraco.Core.Logging;
using Umbraco.Core.Manifest;
using Umbraco.Core.Models.Mapping;
using Umbraco.Core.Models.PublishedContent;
using Umbraco.Core.Persistence;
using Umbraco.Core.Persistence.Mappers;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Persistence.SqlSyntax;
using Umbraco.Core.Plugins;
using Umbraco.Core.PropertyEditors;
using Umbraco.Core.Services;
using Umbraco.Core.Strings;
using Umbraco.Tests.TestHelpers;
using Umbraco.Tests.TestHelpers.Stubs;
using Umbraco.Web;
using Umbraco.Web.DI;
using Umbraco.Web.Services;
using UmbracoExamine;
using Current = Umbraco.Core.DI.Current;

namespace Umbraco.Tests.Testing
{
    /// <summary>
    /// Provides the top-level base class for all Umbraco integration tests.
    /// </summary>
    /// <remarks>
    /// True unit tests do not need to inherit from this class, but most of Umbraco tests
    /// are not true unit tests but integration tests requiring services, databases, etc. This class
    /// provides all the necessary environment, through DI. Yes, DI is bad in tests - unit tests.
    /// But it is OK in integration tests.
    /// </remarks>
    public abstract class UmbracoTestBase
    {
        // this class
        // ensures that Current is properly resetted
        // ensures that a service container is properly initialized and disposed
        // compose the required dependencies according to test options (UmbracoTestAttribute)
        //
        // everything is virtual (because, why not?)
        // starting a test runs like this:
        // - SetUp() // when overriding, call base.SetUp() *first* then setup your own stuff
        // --- Compose() // when overriding, call base.Commpose() *first* then compose your own stuff
        // --- Initialize() // same
        // - test runs
        // - TearDown() // when overriding, clear you own stuff *then* call base.TearDown()
        //
        // about attributes
        //
        // this class defines the SetUp and TearDown methods, with proper attributes, and
        // these attributes are *inherited* so classes inheriting from this class should *not*
        // add the attributes to SetUp nor TearDown again
        //
        // this class is *not* marked with the TestFeature attribute because it is *not* a
        // test feature, and no test "base" class should be. only actual test feature classes
        // should be marked with that attribute.

        protected ServiceContainer Container { get; private set; }

        protected UmbracoTestAttribute Options { get; private set; }

        protected static bool FirstTestInSession = true;

        protected bool FirstTestInFixture = true;

        internal TestObjects TestObjects { get; private set; }

        private static PluginManager _pluginManager;

        #region Accessors

        protected ILogger Logger => Container.GetInstance<ILogger>();

        protected IProfiler Profiler => Container.GetInstance<IProfiler>();

        protected ProfilingLogger ProfilingLogger => Container.GetInstance<ProfilingLogger>();

        protected CacheHelper CacheHelper => Container.GetInstance<CacheHelper>();

        protected virtual ISqlSyntaxProvider SqlSyntax => Container.GetInstance<ISqlSyntaxProvider>();

        protected IMapperCollection Mappers => Container.GetInstance<IMapperCollection>();

        protected IQueryFactory QueryFactory => Container.GetInstance<DatabaseContext>().QueryFactory;

        #endregion

        #region Setup

        [SetUp]
        public virtual void SetUp()
        {
            // should not need this if all other tests were clean
            // but hey, never know, better avoid garbage-in
            Reset();

            Container = new ServiceContainer();
            Container.ConfigureUmbracoCore();

            TestObjects = new TestObjects(Container);

            // get/merge the attributes marking the method and/or the classes
            var testName = TestContext.CurrentContext.Test.Name;
            var pos = testName.IndexOf('(');
            if (pos > 0) testName = testName.Substring(0, pos);
            Options = UmbracoTestAttribute.Get(GetType().GetMethod(testName));

            Compose();
            Initialize();
        }

        protected virtual void Compose()
        {
            ComposeLogging(Options.Logger);
            ComposeCacheHelper();
            ComposeAutoMapper(Options.AutoMapper);
            ComposePluginManager(Options.ResetPluginManager);
            ComposeDatabase(Options.Database);
            ComposeApplication(Options.WithApplication);
            // etc
            ComposeWtf();

            // not sure really
            var composition = new Composition(Container, RuntimeLevel.Run);
            Compose(composition);
        }

        protected virtual void Compose(Composition composition)
        { }

        protected virtual void Initialize()
        {
            InitializeAutoMapper(Options.AutoMapper);
            InitializeApplication(Options.WithApplication);
        }

        #endregion

        #region Compose

        protected virtual void ComposeLogging(UmbracoTestOptions.Logger option)
        {
            if (option == UmbracoTestOptions.Logger.Mock)
            {
                Container.RegisterSingleton(f => Mock.Of<ILogger>());
                Container.RegisterSingleton(f => Mock.Of<IProfiler>());
            }
            else if (option == UmbracoTestOptions.Logger.Log4Net)
            {
                Container.RegisterSingleton<ILogger>(f => new Logger(new FileInfo(TestHelper.MapPathForTest("~/unit-test-log4net.config"))));
                Container.RegisterSingleton<IProfiler>(f => new LogProfiler(f.GetInstance<ILogger>()));
            }

            Container.RegisterSingleton(f => new ProfilingLogger(f.GetInstance<ILogger>(), f.GetInstance<IProfiler>()));
        }

        protected virtual void ComposeWtf()
        {
            // imported from TestWithSettingsBase
            // which was inherited by TestWithApplicationBase so pretty much used everywhere
            Umbraco.Web.Current.UmbracoContextAccessor = new TestUmbracoContextAccessor();
        }

        protected virtual void ComposeCacheHelper()
        {
            Container.RegisterSingleton(f => CacheHelper.CreateDisabledCacheHelper());
            Container.RegisterSingleton(f => f.GetInstance<CacheHelper>().RuntimeCache);
        }

        protected virtual void ComposeAutoMapper(bool configure)
        {
            if (configure == false) return;

            Container.RegisterFrom<CoreModelMappersCompositionRoot>();
            Container.RegisterFrom<WebModelMappersCompositionRoot>();
        }

        protected virtual void ComposePluginManager(bool reset)
        {
            Container.RegisterSingleton(f =>
            {
                if (_pluginManager != null && reset == false) return _pluginManager;

                return _pluginManager = new PluginManager(f.GetInstance<CacheHelper>().RuntimeCache, f.GetInstance<ProfilingLogger>(), false)
                {
                    AssembliesToScan = new[]
                    {
                        Assembly.Load("Umbraco.Core"),
                        Assembly.Load("umbraco"),
                        Assembly.Load("Umbraco.Tests"),
                        Assembly.Load("cms"),
                        Assembly.Load("controls"),
                    }
                };
            });
        }

        protected virtual void ComposeDatabase(UmbracoTestOptions.Database option)
        {
            if (option == UmbracoTestOptions.Database.None) return;

            // create the file
            // create the schema

        }

        protected virtual void ComposeApplication(bool withApplication)
        {
            if (withApplication == false) return;

            var settings = SettingsForTests.GetDefault();

            // default Datalayer/Repositories/SQL/Database/etc...
            Container.RegisterFrom<RepositoryCompositionRoot>();

            // register basic stuff that might need to be there for some container resolvers to work
            Container.RegisterSingleton(factory => SettingsForTests.GetDefault());
            Container.RegisterSingleton(factory => settings.Content);
            Container.RegisterSingleton(factory => settings.Templates);
            Container.Register<IServiceProvider, ActivatorServiceProvider>();
            Container.Register(factory => new MediaFileSystem(Mock.Of<IFileSystem>()));
            Container.RegisterSingleton<IExamineIndexCollectionAccessor, TestIndexCollectionAccessor>();

            // replace some stuff
            Container.RegisterSingleton(factory => Mock.Of<IFileSystem>(), "ScriptFileSystem");
            Container.RegisterSingleton(factory => Mock.Of<IFileSystem>(), "PartialViewFileSystem");
            Container.RegisterSingleton(factory => Mock.Of<IFileSystem>(), "PartialViewMacroFileSystem");
            Container.RegisterSingleton(factory => Mock.Of<IFileSystem>(), "StylesheetFileSystem");

            // need real file systems here as templates content is on-disk only
            //Container.RegisterSingleton<IFileSystem>(factory => Mock.Of<IFileSystem>(), "MasterpageFileSystem");
            //Container.RegisterSingleton<IFileSystem>(factory => Mock.Of<IFileSystem>(), "ViewFileSystem");
            Container.RegisterSingleton<IFileSystem>(factory => new PhysicalFileSystem("Views", "/views"), "ViewFileSystem");
            Container.RegisterSingleton<IFileSystem>(factory => new PhysicalFileSystem("MasterPages", "/masterpages"), "MasterpageFileSystem");

            // no factory (noop)
            Container.RegisterSingleton<IPublishedContentModelFactory, NoopPublishedContentModelFactory>();

            // register application stuff (database factory & context, services...)
            Container.RegisterCollectionBuilder<MapperCollectionBuilder>()
                .AddCore();

            Container.RegisterSingleton<IEventMessagesFactory>(_ => new TransientEventMessagesFactory());
            Container.RegisterSingleton<IDatabaseScopeAccessor, TestDatabaseScopeAccessor>();
            var sqlSyntaxProviders = TestObjects.GetDefaultSqlSyntaxProviders(Logger);
            Container.RegisterSingleton<ISqlSyntaxProvider>(_ => sqlSyntaxProviders.OfType<SqlCeSyntaxProvider>().First());
            Container.RegisterSingleton<IDatabaseFactory>(f => new UmbracoDatabaseFactory(
                Core.Configuration.GlobalSettings.UmbracoConnectionName,
                sqlSyntaxProviders,
                Logger, f.GetInstance<IDatabaseScopeAccessor>(),
                Mock.Of<IMapperCollection>()));
            Container.RegisterSingleton(f => new DatabaseContext(f.GetInstance<IDatabaseFactory>()));

            Container.RegisterCollectionBuilder<UrlSegmentProviderCollectionBuilder>(); // empty
            Container.Register(factory
                => TestObjects.GetDatabaseUnitOfWorkProvider(factory.GetInstance<ILogger>(), factory.TryGetInstance<IDatabaseFactory>(), factory.TryGetInstance<RepositoryFactory>()));

            Container.RegisterFrom<ServicesCompositionRoot>();
            // composition root is doing weird things, fix
            Container.RegisterSingleton<IApplicationTreeService, ApplicationTreeService>();
            Container.RegisterSingleton<ISectionService, SectionService>();

            // somehow property editor ends up wanting this
            Container.RegisterSingleton(f => new ManifestBuilder(
                f.GetInstance<IRuntimeCacheProvider>(),
                new ManifestParser(f.GetInstance<ILogger>(), new DirectoryInfo(IOHelper.MapPath("~/App_Plugins")), f.GetInstance<IRuntimeCacheProvider>())
            ));

            // note - don't register collections, use builders
            Container.RegisterCollectionBuilder<PropertyEditorCollectionBuilder>();
        }

        #endregion

        #region Initialize

        protected virtual void InitializeAutoMapper(bool configure)
        {
            if (configure == false) return;

            Mapper.Initialize(configuration =>
            {
                var mappers = Container.GetAllInstances<ModelMapperConfiguration>();
                foreach (var mapper in mappers)
                    mapper.ConfigureMappings(configuration);
            });
        }

        protected virtual void InitializeApplication(bool withApplication)
        {
            if (withApplication == false) return;

            TestHelper.InitializeContentDirectories();

            // initialize legacy mapings for core editors
            // create the legacy prop-eds mapping
            if (LegacyPropertyEditorIdToAliasConverter.Count() == 0)
                LegacyPropertyEditorIdToAliasConverter.CreateMappingsForCoreEditors();
        }

        #endregion

        #region TearDown and Reset

        [TearDown]
        public virtual void TearDown()
        {
            FirstTestInFixture = false;
            FirstTestInSession = false;

            Reset();

            if (Options.WithApplication)
            {
                TestHelper.CleanContentDirectories();
                TestHelper.CleanUmbracoSettingsConfig();
            }
        }

        protected virtual void Reset()
        {
            Current.Reset();

            Container?.Dispose();
            Container = null;

            // reset all other static things that should not be static ;(
            UriUtility.ResetAppDomainAppVirtualPath();
            SettingsForTests.Reset(); // fixme - should it be optional?
        }

        #endregion
    }
}