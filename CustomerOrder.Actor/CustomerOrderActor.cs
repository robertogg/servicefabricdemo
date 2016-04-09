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

    internal class CustomerOrderActor : Actor, ICustomerOrderActor, IRemindable
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

        public async Task ReceiveReminderAsync(string reminderName, byte[] context, TimeSpan dueTime, TimeSpan period)
        {
            switch (reminderName)
            {
                case CustomerOrderReminderNames.FulfillOrderReminder:

                    await this.FulfillOrderAsync();

                    CustomerOrderStatus orderStatus = await this.GetOrderStatusAsync();

                    if (orderStatus == CustomerOrderStatus.Shipped || orderStatus == CustomerOrderStatus.Canceled)
                    {
                        //Remove fulfill order reminder so Actor can be gargabe collected.
                        IActorReminder orderReminder = this.GetReminder(CustomerOrderReminderNames.FulfillOrderReminder);
                        await this.UnregisterReminderAsync(orderReminder);
                    }

                    break;

                default:
                    // We should never arrive here normally. The system won't call reminders that don't exist. 
                    // But for our own sake in case we add a new reminder somewhere and forget to handle it, this will remind us.
                    throw new InvalidOperationException("Unknown reminder: " + reminderName);
            }
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

        internal async Task FulfillOrderAsync()
        {
            ServiceUriBuilder builder = new ServiceUriBuilder(InventoryServiceName);

            await this.SetOrderStatusAsync(CustomerOrderStatus.InProcess);

            IList<CustomerOrderItem> orderedItems = await this.StateManager.GetStateAsync<IList<CustomerOrderItem>>(OrderItemListPropertyName);
           
            foreach (CustomerOrderItem item in orderedItems.Where(x => x.FulfillmentRemaining > 0))
            {
                IInventoryService inventoryService = ServiceProxy.Create<IInventoryService>(builder.ToUri(), item.ItemId.GetPartitionKey());

                //First, check the item is listed in inventory.  
                //This will avoid infinite backorder status.
                if ((await inventoryService.IsItemInInventoryAsync(item.ItemId, this.tokenSource.Token)) == false)
                {
                    await this.SetOrderStatusAsync(CustomerOrderStatus.Canceled);
                    return;
                }

                int numberItemsRemoved =
                    await
                        inventoryService.RemoveStockAsync(
                            item.ItemId,
                            item.Quantity,
                            new CustomerOrderActorMessageId(
                                new ActorId(this.Id.GetGuidId()),
                                await this.StateManager.GetStateAsync<long>(RequestIdPropertyName)));

                item.FulfillmentRemaining -= numberItemsRemoved;
            }

            IList<CustomerOrderItem> items = await this.StateManager.GetStateAsync<IList<CustomerOrderItem>>(OrderItemListPropertyName);
            bool backordered = false;

            foreach (CustomerOrderItem item in items)
            {
                if (item.FulfillmentRemaining > 0)
                {
                    backordered = true;
                    break;
                }
            }

            if (backordered)
            {
                await this.SetOrderStatusAsync(CustomerOrderStatus.Backordered);
            }
            else
            {
                await this.SetOrderStatusAsync(CustomerOrderStatus.Shipped);
            }

            ActorEventSource.Current.ActorMessage(
                this,
                "{0}; Fulfilled: {1}. Backordered: {2}",
                await this.GetOrderStatusAsStringAsync(),
                items.Count(x => x.FulfillmentRemaining == 0),
                items.Count(x => x.FulfillmentRemaining > 0));

            long messageRequestId = await this.StateManager.GetStateAsync<long>(RequestIdPropertyName);
            await this.StateManager.SetStateAsync<long>(RequestIdPropertyName, ++messageRequestId);
        }
    }
}