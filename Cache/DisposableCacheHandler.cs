using System.Collections.Generic;
namespace qb.Cache
{
    public abstract class DisposableCacheHandler
    {
        protected object ownersLock = new object();
        protected List<object> owners = new List<object>();
        public int UseCount
        {
            get
            {
                lock (ownersLock)
                    owners.RemoveAll(x => x.Equals(null));
                return owners.Count;
            }
        }
        /// <summary>
        /// Release an handle from an owner
        /// </summary>
        /// <param name="owner">
        /// The owner object binded with the handle.
        /// To drive an usage mechanism each load is binded with an owner.
        /// When there are no more valid owner binded with the handler the managed textures are marked as 
        /// not use and can be disposed from cache with the static method DisposeUnusedTextures        /// </param>
        /// </param>        
        /// <param name="disposeIfNoMoreOwned">
        /// Flag that indicate if the Dispose method must be call in case of no more binded
        /// </param>
        public virtual void Release(object owner, bool disposeIfNoMoreOwned = false)
        {
            lock (ownersLock)
            {
                if (owner == null)
                    owners.RemoveAll(x => x.Equals(null));
                else
                    if (owners.Contains(owner))
                {
                    owners.Remove(owner);
                }
                if (disposeIfNoMoreOwned && owners.Count == 0)
                {
                    Dispose();
                }
            }
        }

        /// <summary>
        /// Clear all invalid owner.
        /// An owner can be invalid in case of owner destroy 
        /// </summary>
        /// <param name="disposeIfNoMoreOwned">
        /// Flag that indicate if the Dispose method must be call in case of no more binded
        /// </param>
        public void ClearInvalidOwners(bool disposeIfNoMoreOwned = false)
        {
            lock (ownersLock)
            {
                owners.RemoveAll(x => x.Equals(null));
                if (disposeIfNoMoreOwned && owners.Count == 0)
                {
                    Dispose();
                }
            }
        }

        protected abstract void Dispose();

    }
}
