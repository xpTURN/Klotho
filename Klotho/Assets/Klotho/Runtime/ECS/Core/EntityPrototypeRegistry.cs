using System;
using System.Collections.Generic;

namespace xpTURN.Klotho.ECS
{
    public class EntityPrototypeRegistry
    {
        private readonly Dictionary<int, IEntityPrototype> _prototypes = new Dictionary<int, IEntityPrototype>();

        public void Register(int prototypeId, IEntityPrototype prototype)
        {
            if (_prototypes.ContainsKey(prototypeId))
                throw new InvalidOperationException($"Prototype ID {prototypeId} is already registered.");
            _prototypes[prototypeId] = prototype;
        }

        internal EntityRef Create(int prototypeId, Frame frame)
        {
            if (!_prototypes.TryGetValue(prototypeId, out var proto))
                throw new ArgumentException($"Unknown prototype ID: {prototypeId}");
            var entity = frame.CreateEntity();
            proto.Apply(frame, entity);
            return entity;
        }
    }
}
