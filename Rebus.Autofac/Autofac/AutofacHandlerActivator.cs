﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Rebus.Activation;
using Rebus.Bus;
using Rebus.Config;
using Rebus.Exceptions;
using Rebus.Extensions;
using Rebus.Handlers;
using Rebus.Pipeline;
using Rebus.Transport;
#pragma warning disable 1998

namespace Rebus.Autofac
{
    class AutofacHandlerActivator : IHandlerActivator
    {
        const string LongExceptionMessage =
            "This particular container builder seems to have had the RegisterRebus(...) extension called on it more than once, which is unfortunately not allowed. In some cases, this is simply an indication that the configuration code for some reason has been executed more than once, which is probably not intended. If you intended to use one Autofac container to host multiple Rebus instances, please consider using a separate container instance for each Rebus endpoint that you wish to start.";

        readonly ConcurrentDictionary<Type, Type[]> _resolveTypes = new ConcurrentDictionary<Type, Type[]>();

        IContainer _container;

        public AutofacHandlerActivator(ContainerBuilder containerBuilder, Action<RebusConfigurer, IComponentContext> configureBus, bool startBus = true)
        {
            if (containerBuilder == null) throw new ArgumentNullException(nameof(containerBuilder));
            if (configureBus == null) throw new ArgumentNullException(nameof(configureBus));

            containerBuilder.RegisterBuildCallback(container =>
            {
                var registrations = container.ComponentRegistry.Registrations;

                if (HasMultipleBusRegistrations(registrations))
                {
                    throw new InvalidOperationException(LongExceptionMessage);
                }

                SetContainer(container);

                if (startBus)
                {
                    StartBus(container);
                }
            });

            containerBuilder
                .Register(context =>
                {
                    var rebusConfigurer = Configure.With(this);
                    configureBus.Invoke(rebusConfigurer, context);
                    return rebusConfigurer.Start();
                })
                .SingleInstance();

            containerBuilder
                .Register(c => c.Resolve<IBus>().Advanced.SyncBus)
                .InstancePerDependency()
                .ExternallyOwned();

            containerBuilder
                .Register(c =>
                {
                    var messageContext = MessageContext.Current;
                    if (messageContext == null)
                    {
                        throw new InvalidOperationException("MessageContext.Current was null, which probably means that IMessageContext was resolve outside of a Rebus message handler transaction");
                    }
                    return messageContext;
                })
                .InstancePerDependency()
                .ExternallyOwned();
        }

        static void StartBus(IContainer c)
        {
            try
            {
                c.Resolve<IBus>();
            }
            catch (Exception exception)
            {
                throw new RebusConfigurationException(exception, "Could not start Rebus");
            }
        }

        static bool HasMultipleBusRegistrations(IEnumerable<IComponentRegistration> registrations) =>
            registrations.SelectMany(r => r.Services)
                .OfType<TypedService>()
                .Count(s => s.ServiceType == typeof(IBus)) > 1;

        /// <summary>
        /// Resolves all handlers for the given <typeparamref name="TMessage"/> message type
        /// </summary>
        public async Task<IEnumerable<IHandleMessages<TMessage>>> GetHandlers<TMessage>(TMessage message, ITransactionContext transactionContext)
        {
            ILifetimeScope CreateLifetimeScope()
            {
                var scope = _container.BeginLifetimeScope();
                transactionContext.OnDisposed(() => scope.Dispose());
                return scope;
            }

            var lifetimeScope = transactionContext
                .GetOrAdd("current-autofac-lifetime-scope", CreateLifetimeScope);

            Type[] FindTypesToResolve(Type messageType)
            {
                var typesToResolve = messageType.GetBaseTypes()
                    .Concat(new[] {messageType})
                    .Select(handledMessageType =>
                    {
                        var implementedInterface = typeof(IHandleMessages<>).MakeGenericType(handledMessageType);
                        var implementedInterfaceSequence = typeof(IEnumerable<>).MakeGenericType(implementedInterface);
                        return implementedInterfaceSequence;
                    });

                return typesToResolve.ToArray();
            }

            var types = _resolveTypes.GetOrAdd(typeof(TMessage), FindTypesToResolve);

            return types.SelectMany(type => (IEnumerable<IHandleMessages<TMessage>>) lifetimeScope.Resolve(type));
        }

        void SetContainer(IContainer container)
        {
            if (_container != null)
            {
                throw new InvalidOperationException("One container instance can only have its SetContainer method called once");
            }
            _container = container ?? throw new ArgumentNullException(nameof(container), "Please pass a container instance when calling this method");
        }
    }
}