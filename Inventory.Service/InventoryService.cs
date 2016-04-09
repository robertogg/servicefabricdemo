// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Inventory.Service
{
    using System;
    using System.Collections.Generic;
    using System.Fabric;
    using System.Fabric.Description;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Common;
    using Inventory.Domain;
    using Microsoft.ServiceFabric.Data;
    using Microsoft.ServiceFabric.Data.Collections;
    using Microsoft.ServiceFabric.Services.Client;
    using Microsoft.ServiceFabric.Services.Communication.Runtime;
    using Microsoft.ServiceFabric.Services.Remoting.Client;
    using Microsoft.ServiceFabric.Services.Remoting.Runtime;
    using Microsoft.ServiceFabric.Services.Runtime;
  

    internal class InventoryService : StatefulService, IInventoryService
    {
        private const string InventoryItemDictionaryName = "inventoryItems";

        /// <summary>
        /// This constructor is used in unit tests to inject a different state manager for unit testing.
        /// </summary>
        /// <param name="stateManager"></param>
        public InventoryService(StatefulServiceContext serviceContext) : this(serviceContext, (new ReliableStateManager(serviceContext)))
        {
        }

        public InventoryService(StatefulServiceContext context, IReliableStateManagerReplica stateManagerReplica) : base(context, stateManagerReplica)
        {
        }

        /// <summary>
        /// Used internally to generate inventory items and adds them to the ReliableDict we have.
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public async Task<bool> CreateInventoryItemAsync(InventoryItem item)
        {
            IReliableDictionary<InventoryItemId, InventoryItem> inventoryItems =
                await this.StateManager.GetOrAddAsync<IReliableDictionary<InventoryItemId, InventoryItem>>(InventoryItemDictionaryName);

            using (ITransaction tx = this.StateManager.CreateTransaction())
            {
                await inventoryItems.AddAsync(tx, item.Id, item);
                await tx.CommitAsync();
                ServiceEventSource.Current.ServiceMessage(this, "Created inventory item: {0}", item);
            }

            return true;
        }

        /// <summary>
        /// Tries to add the given quantity to the inventory item with the given ID without going over the maximum quantity allowed for an item.
        /// </summary>
        /// <param name="itemId"></param>
        /// <param name="quantity"></param>
        /// <returns>The quantity actually added to the item.</returns>
        public async Task<int> AddStockAsync(InventoryItemId itemId, int quantity)
        {
            IReliableDictionary<InventoryItemId, InventoryItem> inventoryItems =
                await this.StateManager.GetOrAddAsync<IReliableDictionary<InventoryItemId, InventoryItem>>(InventoryItemDictionaryName);

            int quantityAdded = 0;

            ServiceEventSource.Current.ServiceMessage(this, "Received add stock request. Item: {0}. Quantity: {1}.", itemId, quantity);

            using (ITransaction tx = this.StateManager.CreateTransaction())
            {
                // Try to get the InventoryItem for the ID in the request.
                ConditionalValue<InventoryItem> item = await inventoryItems.TryGetValueAsync(tx, itemId);

                // We can only update the stock for InventoryItems in the system - we are not adding new items here.
                if (item.HasValue)
                {
                    quantityAdded = item.Value.AddStock(quantity);
                    await inventoryItems.SetAsync(tx, item.Value.Id, item.Value);
                }

                await tx.CommitAsync();

                ServiceEventSource.Current.ServiceMessage(
                    this,
                    "Add stock complete. Item: {0}. Added: {1}. Total: {2}",
                    item.Value.Id,
                    quantityAdded,
                    item.Value.AvailableStock);
            }
            return quantityAdded;
        }

        /// <summary>
        /// Removes the given quantity of stock from an in item in the inventory.
        /// </summary>
        /// <param name="request"></param>
        /// <returns>int: Returns the quantity removed from stock.</returns>
        public async Task<int> RemoveStockAsync(InventoryItemId itemId, int quantity, CustomerOrderActorMessageId amId)
        {
            int removed = 0;
            ServiceEventSource.Current.ServiceMessage(this, "inside remove stock {0}|{1}", amId.GetHashCode(), amId.GetHashCode());

            IReliableDictionary<InventoryItemId, InventoryItem> inventoryItems =
                await this.StateManager.GetOrAddAsync<IReliableDictionary<InventoryItemId, InventoryItem>>(InventoryItemDictionaryName);

            using (ITransaction tx = this.StateManager.CreateTransaction())
            {
               
                    ConditionalValue<InventoryItem> item = await inventoryItems.TryGetValueAsync(tx, itemId);

                if (item.HasValue)
                {
                    removed = item.Value.RemoveStock(quantity);

                    await inventoryItems.SetAsync(tx, itemId, item.Value);

                    ServiceEventSource.Current.ServiceMessage(
                        this,
                        "Removed stock complete. Item: {0}. Removed: {1}. Remaining: {2}",
                        item.Value.Id,
                        removed,
                        item.Value.AvailableStock);

                    await tx.CommitAsync();
                    ServiceEventSource.Current.Message("Inventory Service Changes Committed");
                    ServiceEventSource.Current.Message("Removed {0} of item {1}", removed, itemId);

                }
            }
            return removed;
        }

        public async Task<bool> IsItemInInventoryAsync(InventoryItemId itemId, CancellationToken ct)
        {
            ServiceEventSource.Current.Message("checking item {0} to see if it is in inventory", itemId);
            IReliableDictionary<InventoryItemId, InventoryItem> inventoryItems =
                await this.StateManager.GetOrAddAsync<IReliableDictionary<InventoryItemId, InventoryItem>>(InventoryItemDictionaryName);

            await this.PrintInventoryItemsAsync(inventoryItems, ct);

            using (ITransaction tx = this.StateManager.CreateTransaction())
            {
                ConditionalValue<InventoryItem> item = await inventoryItems.TryGetValueAsync(tx, itemId);
                return item.HasValue;
            }
        }

        public async Task<IEnumerable<InventoryItemView>> GetCustomerInventoryAsync(CancellationToken ct)
        {
            IReliableDictionary<InventoryItemId, InventoryItem> inventoryItems =
                await this.StateManager.GetOrAddAsync<IReliableDictionary<InventoryItemId, InventoryItem>>(InventoryItemDictionaryName);

            ServiceEventSource.Current.Message("Called GetCustomerInventory to return InventoryItemView");

            await this.PrintInventoryItemsAsync(inventoryItems, ct);

            IList<InventoryItemView> results = new List<InventoryItemView>();

            using (ITransaction tx = this.StateManager.CreateTransaction())
            {
                ServiceEventSource.Current.Message("Generating item views for {0} items", await inventoryItems.GetCountAsync(tx));

                IAsyncEnumerator<KeyValuePair<InventoryItemId, InventoryItem>> enumerator =
                    (await inventoryItems.CreateEnumerableAsync(tx)).GetAsyncEnumerator();

                while (await enumerator.MoveNextAsync(ct))
                {
                    if (enumerator.Current.Value.AvailableStock > 0)
                    {
                        results.Add(enumerator.Current.Value);
                    }
                }
            }


            return results;
        }

        /// <summary>
        /// NOTE: This should not be used in published MVP code. 
        /// This function allows us to remove inventory items from inventory.
        /// </summary>
        /// <param name="itemId"></param>
        /// <returns></returns>
        public async Task DeleteInventoryItemAsync(InventoryItemId itemId)
        {
            IReliableDictionary<InventoryItemId, InventoryItem> inventoryItems =
                await this.StateManager.GetOrAddAsync<IReliableDictionary<InventoryItemId, InventoryItem>>(InventoryItemDictionaryName);

            using (ITransaction tx = this.StateManager.CreateTransaction())
            {
                await inventoryItems.TryRemoveAsync(tx, itemId);
                await tx.CommitAsync();
            }
        }

        /// <summary>
        /// Creates a new communication listener
        /// </summary>
        /// <returns></returns>
        protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            return new[]
            {
                new ServiceReplicaListener(context => this.CreateServiceRemotingListener(context))
            };
        }

        protected override Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                ServiceEventSource.Current.ServiceMessage(this, "inside RunAsync for Inventory Service");

                return Task.FromResult(true);
            }
            catch (Exception e)
            {
                ServiceEventSource.Current.ServiceMessage(this, "RunAsync Failed, {0}", e);
                throw;
            }
        }
        
        private async Task PrintInventoryItemsAsync(IReliableDictionary<InventoryItemId, InventoryItem> inventoryItems, CancellationToken cancellationToken)
        {
            using (ITransaction tx = this.StateManager.CreateTransaction())
            {
                ServiceEventSource.Current.Message("Printing Inventory for {0} items:", await inventoryItems.GetCountAsync(tx));

                IAsyncEnumerator<KeyValuePair<InventoryItemId, InventoryItem>> enumerator =
                    (await inventoryItems.CreateEnumerableAsync(tx)).GetAsyncEnumerator();

                while (await enumerator.MoveNextAsync(cancellationToken))
                {
                    ServiceEventSource.Current.Message("ID:{0}|Item:{1}", enumerator.Current.Key, enumerator.Current.Value);
                }
            }
        }
        private enum BackupManagerType
        {
            Azure,
            Local,
            None
        };
    }
}