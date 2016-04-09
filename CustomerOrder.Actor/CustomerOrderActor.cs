// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace CustomerOrder.Actor
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Common;
    using CustomerOrder.Domain;
    using Inventory.Domain;
    using Microsoft.ServiceFabric.Actors;
    using Microsoft.ServiceFabric.Actors.Runtime;
    using Microsoft.ServiceFabric.Data;
    using Microsoft.ServiceFabric.Services.Remoting.Client;

    internal class CustomerOrderActor : Actor, ICustomerOrderActor
    {
        private const string InventoryServiceName = "InventoryService";
        private const string OrderItemListPropertyName = "OrderList";
        private const string OrderStatusPropertyName = "CustomerOrderStatus";
        private const string RequestIdPropertyName = "RequestId";

        private CancellationTokenSource tokenSource = null;

        public async Task SubmitOrderAsync(IEnumerable<CustomerOrderItem> orderList)
        {
            try
            {
                await this.StateManager.SetStateAsync<List<CustomerOrderItem>>(OrderItemListPropertyName, new List<CustomerOrderItem>(orderList));
                await this.StateManager.SetStateAsync<CustomerOrderStatus>(OrderStatusPropertyName, CustomerOrderStatus.Submitted);

                await this.RegisterReminderAsync(
                    CustomerOrderReminderNames.FulfillOrderReminder,
                    null,
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(10));
            }
            catch (Exception e)
            {
                ActorEventSource.Current.Message(e.ToString());
            }

            ActorEventSource.Current.Message("Order submitted with {0} items", orderList.Count());

            return;
        }

        public async Task<string> GetOrderStatusAsStringAsync()
        {
            return (await this.GetOrderStatusAsync()).ToString();
        }

        protected override Task OnDeactivateAsync()
        {
            this.tokenSource.Cancel();
            this.tokenSource.Dispose();
            return Task.FromResult(true);
        }

        protected override async Task OnActivateAsync()
        {
            this.tokenSource = new CancellationTokenSource();

            CustomerOrderStatus orderStatusResult = await this.GetOrderStatusAsync();

            if (orderStatusResult == CustomerOrderStatus.Unknown)
            {
                await this.StateManager.SetStateAsync<List<CustomerOrderItem>>(OrderItemListPropertyName, new List<CustomerOrderItem>());
                await this.StateManager.SetStateAsync<long>(RequestIdPropertyName, 0);
                await this.SetOrderStatusAsync(CustomerOrderStatus.New);
            }

            return;
        }

        private async Task<CustomerOrderStatus> GetOrderStatusAsync()
        {
            ConditionalValue<CustomerOrderStatus> orderStatusResult = await this.StateManager.TryGetStateAsync<CustomerOrderStatus>(OrderStatusPropertyName);
            if (orderStatusResult.HasValue)
            {
                return orderStatusResult.Value;
            }
            else
            {
                return CustomerOrderStatus.Unknown;
            }
        }

        private async Task SetOrderStatusAsync(CustomerOrderStatus orderStatus)
        {
            await this.StateManager.SetStateAsync<CustomerOrderStatus>(OrderStatusPropertyName, orderStatus);
        }
    }
}