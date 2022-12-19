﻿using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Assertions;

namespace ObjectPool
{
    /// <summary>
    /// A collection of pools that creates and tracks instances of multiple prefabs.
    /// </summary>
    public class PrefabPoolCollection : IDisposable
    {
        private readonly Dictionary<Component, PrefabPool> prefabPools = new Dictionary<Component, PrefabPool>();

        private readonly Transform root;
        private bool isDisposed;
        
        public PrefabPoolCollection(Transform root)
        {
            this.root = root;
        }
        
        #region Public API
        
        public T Acquire<T>(T prefab) where T : Component
        {
            return Acquire(prefab, null, Vector3.zero, Quaternion.identity);
        }
        
        public T Acquire<T>(T prefab, Transform parent) where T : Component
        {
            return Acquire(prefab, parent, Vector3.zero, Quaternion.identity);

        }
        
        public T Acquire<T>(T prefab, Vector3 localPosition, Quaternion localRotation) where T : Component
        {
            return Acquire(prefab, null, localPosition, localRotation);
        }
        
        public T Acquire<T>(T prefab, Transform parent, Vector3 localPosition, Quaternion localRotation) where T : Component
        {
            return AcquireInternal(prefab, true, parent, localPosition, localRotation);
        }
        
        public T AcquireDisabled<T>(T prefab) where T : Component
        {
            return AcquireDisabled(prefab, null, Vector3.zero, Quaternion.identity);
        }
        
        public T AcquireDisabled<T>(T prefab, Transform parent) where T : Component
        {
            return AcquireDisabled(prefab, parent, Vector3.zero, Quaternion.identity);

        }
        
        public T AcquireDisabled<T>(T prefab, Vector3 localPosition, Quaternion localRotation) where T : Component
        {
            return AcquireDisabled(prefab, null, localPosition, localRotation);
        }
        
        public T AcquireDisabled<T>(T prefab, Transform parent, Vector3 localPosition, Quaternion localRotation) where T : Component
        {
            return AcquireInternal(prefab, false, parent, localPosition, localRotation);
        }

        public void PreWarm<T>(T prefab, int capacity) where T : Component
        {
            Assert.IsFalse(isDisposed);
            
            var pool = GetOrCreatePool(prefab);
            pool.PreWarm(capacity);
        }
        
        public void Return<T>(T instance) where T : Component
        {
            // Returning doesn't actually require any data from PrefabPoolCollection,
            // but is included here so that Acquire() and Return() are accessible from the same API
            PooledObject.Return(instance);
        }

        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            foreach (var pool in prefabPools.Values)
            {
                pool.Dispose();
            }
            
            prefabPools.Clear();

            isDisposed = true;
        }

        #endregion
        
        #region Internals
        
        private T AcquireInternal<T>(T prefab, bool activate, Transform parent, Vector3 localPosition, Quaternion localRotation) where T : Component
        {
            Assert.IsFalse(isDisposed);
            
            var pool = GetOrCreatePool(prefab);
            var instance = pool.Acquire();
            
            if (parent != null)
            {
                instance.transform.SetParent(parent);
            }

            instance.transform.localPosition = localPosition;
            instance.transform.localRotation = localRotation;

            if (activate)
            {
                instance.gameObject.SetActive(true);
            }

            return (T) instance;
        }
        
        #endregion
        
        #region Helpers
        
        private PrefabPool GetOrCreatePool(Component prefab)
        {
            if (!prefabPools.TryGetValue(prefab, out var pool))
            {
                pool = new PrefabPool(prefab, root);
                prefabPools[prefab] = pool;
            }

            return pool;
        }
        
        #endregion
    }
}