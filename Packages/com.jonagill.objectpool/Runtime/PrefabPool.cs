﻿using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Assertions;
using Debug = UnityEngine.Debug;

namespace ObjectPool
{
    public abstract class PrefabPool : IPool
    {
        public abstract int TotalCount { get; }
        public abstract int ActiveCount { get; }
        public abstract int ReserveCount { get; }
        
        public abstract void Return(GameObject gameObject);
        public abstract void Clear();
        public abstract void Dispose();
    }
    
    /// <summary>
    /// A pool that creates and tracks instances of a single prefab.
    /// </summary>
    public class PrefabPool<T> : PrefabPool, IPool<T> where T : Component
    {
        public override int TotalCount => ActiveCount + ReserveCount;
        public override int ActiveCount => activeInstances.Count;
        public override int ReserveCount => reserveInstances.Count;

        private readonly T prefab;
        private readonly Transform poolRoot;
        private bool isDisposed;

        private readonly List<PooledPrefabInstance<T>> activeInstances = new List<PooledPrefabInstance<T>>();
        private readonly Stack<T> reserveInstances = new Stack<T>();

        private readonly bool hasPooledComponents;
        private readonly Dictionary<GameObject, IPooledComponent[]> pooledComponentMap =
            new Dictionary<GameObject, IPooledComponent[]>();

        private readonly bool hasTrailRenderers;
        private readonly Dictionary<GameObject, TrailRenderer[]> trailRendererMap =
            new Dictionary<GameObject, TrailRenderer[]>();
        
        /// <summary>
        /// Create a new prefab pool. Will create a new GameObject under the root
        /// transform under which all prefab instances will be constructed
        /// </summary>
        public PrefabPool(T prefab, Transform root)
        {
            Assert.IsNotNull(prefab);
            Assert.IsNotNull(root);

            this.prefab = prefab;
            hasPooledComponents = prefab.GetComponentInChildren<IPooledComponent>(true) != null;
            hasTrailRenderers = prefab.GetComponentInChildren<TrailRenderer>( true ) != null;

            poolRoot = new GameObject($"PrefabPool ({prefab.name})").transform;
            poolRoot.SetParent(root, worldPositionStays: false);
        }

        /// <summary>
        /// Retrieve an instance of the configured prefab from the pool, creating a new instance if necessary.
        /// Returns the instance as disabled so that the invoking system can control when to re-enable the instance.
        /// </summary>
        public PooledInstance<T> Acquire()
        {
            Assert.IsFalse(isDisposed);
            Assert.IsNotNull( poolRoot );

            T instance = null;
            if (reserveInstances.Count > 0)
            {
                instance = reserveInstances.Pop();
            }
            else
            {
                instance = CreateInstance();
            }

            var pooledInstance = new PooledPrefabInstance<T>(instance, this);

            // Track the new instance
            activeInstances.Add(pooledInstance);
            
            // Unparent from the disabled root
            instance.transform.SetParent( null, worldPositionStays: false );

            // Run editor check to make sure no one is adding or removing IPooledComponents after acquisition
            EditorEnsurePooledComponentsMatchCachedValues( instance.gameObject );

            // Notify any IPooledComponents that they've been acquired
            if (hasPooledComponents)
            {
                var pooledComponents = pooledComponentMap[instance.gameObject];
                foreach (var component in pooledComponents)
                {
                    if (component != null)
                    {
                        component.OnAcquire();
                    }
                }
            }
            
            // Clear any old vertices from our trail renderers
            if ( hasTrailRenderers )
            {
                var trailRenderers = trailRendererMap[instance.gameObject];
                foreach ( var renderer in trailRenderers )
                {
                    renderer.Clear();
                }
            }

            // Do not re-activate the instance -- leave that for the invoker to decide when to activate the instance
            return pooledInstance;
        }
        
        public override void Return(GameObject gameObject)
        {
            Assert.IsNotNull( gameObject );
            var instance = gameObject.GetComponent<T>();
            Assert.IsNotNull( instance );

            Return( instance );
        }

        public void Return(T instance)
        {
            if (isDisposed)
            {
                // We can't return this instance to our pool -- just destroy it
                Object.Destroy(instance.gameObject);
                return;
            }
            
            Assert.IsNotNull( instance );
            Assert.IsNotNull( poolRoot );
            
            if ( reserveInstances.Contains( instance ) )
            {
                // Someone else has already returned this object
                return;
            }

#if UNITY_ASSERTIONS
            // Check that this is the right pool to be returning to
            var pooledObject = instance.GetComponent<PooledObject>();
            Assert.IsNotNull(
                pooledObject,
                $"Component {instance} cannot be returned as it was not instantiated by a pool.");
            Assert.AreEqual(
                this,
                pooledObject.Pool,
                $"Component {instance} cannot be returned as it was instantiated by a different pool.");
            Assert.AreEqual(
                prefab.GetType(),
                instance.GetType(),
                $"Component {instance} cannot be returned as it is of type {instance.GetType()}, but this pool expects type {prefab.GetType()}.");
#endif
            
            // Notify any IPooledComponents that they're being returned
            if (hasPooledComponents)
            {
                var pooledComponents = pooledComponentMap[instance.gameObject];
                foreach (var component in pooledComponents)
                {
                    if (component != null)
                    {
                        component.OnReturn();    
                    }
                }
            }
            
            // Disable the object again so we don't pay additional costs for reparenting (e.g. recalculating UI layouts)
            instance.gameObject.SetActive(false);

            // Reparent under the disabled root
            instance.transform.SetParent(poolRoot, false);
            
            // Search from the back of the collection to the front so that frequently pooled and unpooled
            // objects don't have to search through the oldest instances first to find themselves
            // Perform this search after we have already processed OnReturn() so that any callbacks
            // that interact with the pool have already occurred
            int instanceIndex = -1;
            for (int i = activeInstances.Count-1; i >= 0; i--)
            {
                if (activeInstances[i].Instance == instance)
                {
                    instanceIndex = i;
                    break;
                }
            }
            
            var pooledInstance = activeInstances[instanceIndex];
            Assert.IsTrue(pooledInstance.IsValid, $"Component {instance} cannot be returned as its lifecycle has already been marked as complete.");

            Assert.IsTrue(instanceIndex >= 0, $"Component {instance} cannot be returned as it is not considered an active instance by this pool.");

            activeInstances.RemoveAt(instanceIndex);
            reserveInstances.Push(instance);
            
            // Mark that this lifecycle has ended
            ((IPooledLifetime) pooledInstance).MarkInvalid();
        }

        /// <summary>
        /// Allocates new instances of the prefab until we have at least the specified capacity spawned.
        /// </summary>
        /// <param name="capacity"></param>
        public void PreWarm(int capacity)
        {
            Assert.IsFalse(isDisposed);

            var needInReserve = capacity - ActiveCount;
            while (reserveInstances.Count < needInReserve)
            {
                reserveInstances.Push(CreateInstance());
            }
        }

        /// <summary>
        /// Destroys all instances of the prefab that are not currently acquired by an external system.
        /// </summary>
        public override void Clear()
        {
            foreach (var instance in reserveInstances)
            {
                if ( hasPooledComponents )
                {
                    pooledComponentMap.Remove( instance.gameObject );
                }
                
                if ( hasTrailRenderers )
                {
                    trailRendererMap.Remove( instance.gameObject );
                }
                
                Object.Destroy(instance.gameObject);
            }
            reserveInstances.Clear();
        }

        public override void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            if (poolRoot)
            {
                Object.Destroy(poolRoot.gameObject);
            }
            activeInstances.Clear();
            reserveInstances.Clear();
            pooledComponentMap.Clear();

            isDisposed = true;
        }

        private T CreateInstance()
        {
            // Instantiate the instance, allowing Awake() and Start() to run
            var instance = Object.Instantiate(prefab, poolRoot);
            instance.gameObject.SetActive(false);
            
            var pooledObject = instance.gameObject.AddComponent<PooledObject>();
            pooledObject.SetPool(this);

            if (hasPooledComponents)
            {
                pooledComponentMap[instance.gameObject] = instance.GetComponentsInChildren<IPooledComponent>(true);
            }
            
            if ( hasTrailRenderers )
            {
                trailRendererMap[instance.gameObject] = instance.GetComponentsInChildren<TrailRenderer>( true );
            }

            return instance;
        }
        
        [Conditional("UNITY_EDITOR")]
        private void EditorEnsurePooledComponentsMatchCachedValues( GameObject instance )
        {
            var pooledComponents = instance.GetComponentsInChildren<IPooledComponent>(true);
            var cachedPooledComponentCount = hasPooledComponents ? pooledComponentMap[instance].Length : 0;
            if ( cachedPooledComponentCount != pooledComponents.Length )
            {
                Debug.LogError( $"Pooled instance {instance} has a different number of {nameof(IPooledComponent)} instances ({pooledComponents.Length}) than its prefab ({cachedPooledComponentCount}).\n" +
                                $"Adding or removing {nameof(IPooledComponent)}s at runtime is not currently supported." );
            }
        }
    }
}
