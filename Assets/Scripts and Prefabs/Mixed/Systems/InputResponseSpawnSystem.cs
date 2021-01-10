﻿using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.NetCode;
using Unity.Networking.Transport.Utilities;
using Unity.Collections;
using Unity.Physics;
using Unity.Jobs;
using UnityEngine;

//InputResponseMovementSystem runs on both the Client and Server
//It is predicted on the client but "decided" on the server
[UpdateInWorld(UpdateInWorld.TargetWorld.ClientAndServer)] 
public class InputResponseSpawnSystem : SystemBase
{
    //We will use the BeginSimulationEntityCommandBufferSystem for our structural changes
    private BeginSimulationEntityCommandBufferSystem m_BeginSimEcb;

    //This is a special NetCode group that provides a "prediction tick" and a fixed "DeltaTime"
    private GhostPredictionSystemGroup m_PredictionGroup;

    //This will save our Bullet prefab to be used to spawn bullets 
    private Entity m_BulletPrefab;

    //We are going to use this for "weapon cooldown"
    private const int k_CoolDownTicksCount = 5;


    protected override void OnCreate()
    {
        //This will grab the BeginSimulationEntityCommandBuffer system to be used in OnUpdate
        m_BeginSimEcb = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();

        //This will grab the BeginSimulationEntityCommandBuffer system to be used in OnUpdate
        m_PredictionGroup = World.GetOrCreateSystem<GhostPredictionSystemGroup>();
        
        //We need to ensure the GhostPrefabCollectionComponent has streamed from the SubScene because
        //it is required for the OnUpdate()
        RequireSingletonForUpdate<GhostPrefabCollectionComponent>();        
    }

    protected override void OnUpdate()
    {

        if (m_BulletPrefab == Entity.Null)
        {
            //We must now grab the prefab by going through the the GhostCollection
            var prefabEntity = GetSingletonEntity<GhostPrefabCollectionComponent>();
            var prefabs = EntityManager.GetBuffer<GhostPrefabBuffer>(prefabEntity);
            //We need this variable so we can CreatePredictedSpawnPrefab below
            var foundPrefab = Entity.Null;
            for (int i = 0; i < prefabs.Length; ++i)
            {   //We go through all the prefabs in the GhostCollection and search for the BulletTag
                if (EntityManager.HasComponent<BulletTag>(prefabs[i].Value))
                    //We found our Bullet prefab and we set it to the local variable
                    //We need to do one more step which is to call CreatePredictedSpawnPrefab
                    foundPrefab = prefabs[i].Value;
            }
            //This is a special component we must attach to any player spawned objects (NetCode requirement)
            m_BulletPrefab = GhostCollectionSystem.CreatePredictedSpawnPrefab(EntityManager, foundPrefab);
            //we must "return" after setting this prefab because if we were to continue into the Job
            //we would run into errors because the variable was JUST set (ECS funny business)
            return;
        }
        
        //We need a CommandBuffer because we will be making structural changes (creating bullet entities)
        var commandBuffer = m_BeginSimEcb.CreateCommandBuffer().AsParallelWriter();

        //Must declare our local variables before the jobs in the .ForEach()
        var bulletVelocity = GetSingleton<GameSettingsComponent>().bulletVelocity;
        var bulletPrefab = m_BulletPrefab;
        //These are special NetCode values needed to work the prediction system
        var deltaTime = m_PredictionGroup.Time.DeltaTime;
        var currentTick = m_PredictionGroup.PredictingTick;

        //We will grab the buffer of player command from the palyer entity
        var inputFromEntity = GetBufferFromEntity<PlayerCommand>(true);

        //We are looking for player entities that have PlayerCommands in their buffer
        Entities
        .WithReadOnly(inputFromEntity)
        .WithAll<PlayerTag, PlayerCommand>()
        .ForEach((Entity entity, int nativeThreadIndex, ref PlayerStateAndOffsetComponent bulletOffset, in Rotation rotation, in Translation position, in VelocityComponent velocityComponent,
                in GhostOwnerComponent ghostOwner, in PredictedGhostComponent prediction) =>
        {
            //Here we check if we SHOULD do the prediction based on the tick, if we shouldn't, we return
            if (!GhostPredictionSystemGroup.ShouldPredict(currentTick, prediction))
                return;

            //We grab the buffer of commands from the player entity
            var input = inputFromEntity[entity];

            //We then grab the Command from the current tick (which is the PredictingTick)
            //if we cannot get it at the current tick we make sure shoot is 0
            //This is where we will store the current tick data
            PlayerCommand inputData;
            if (!input.GetDataAtTick(currentTick, out inputData))
                inputData.shoot = 0;

            //Here we add the destroy tag to the player if the self-destruct button was pressed
            if (inputData.selfDestruct == 1)
            {  
                commandBuffer.AddComponent<DestroyTag>(nativeThreadIndex, entity);
            }

            var canShoot = bulletOffset.WeaponCooldown == 0 || SequenceHelpers.IsNewer(currentTick, bulletOffset.WeaponCooldown);
            if (inputData.shoot != 0 && canShoot)
            {
                // We create the bullet here
                var bullet = commandBuffer.Instantiate(nativeThreadIndex, bulletPrefab);

                //we set the bullets position as the player's position + the bullet spawn offset
                //math.mul(rotation.Value,bulletOffset.Value) finds the position of the bullet offset in the given rotation
                //think of it as finding the LocalToParent of the bullet offset (because the offset needs to be rotated in the players direction)
                var newPosition = new Translation {Value = position.Value + math.mul(rotation.Value, bulletOffset.Value).xyz};

                // bulletVelocity * math.mul(rotation.Value, new float3(0,0,1)).xyz) takes linear direction of where facing and multiplies by velocity
                // adding to the players physics Velocity makes sure that it takes into account the already existing player velocity (so if shoot backwards while moving forwards it stays in place)
                var vel = new PhysicsVelocity {Linear = (bulletVelocity * math.mul(rotation.Value, new float3(0,0,1)).xyz) + velocityComponent.Linear};

                commandBuffer.SetComponent(nativeThreadIndex, bullet, newPosition);
                commandBuffer.SetComponent(nativeThreadIndex, bullet, vel);
                commandBuffer.SetComponent(nativeThreadIndex, bullet,
                    new GhostOwnerComponent {NetworkId = ghostOwner.NetworkId});

                commandBuffer.AddComponent(nativeThreadIndex, bullet, new PredictedGhostSpawnRequestComponent());

                bulletOffset.WeaponCooldown = currentTick + k_CoolDownTicksCount;
            }

        }).ScheduleParallel();

        //We must add our dependency to the CommandBuffer because we made structural changes
        m_BeginSimEcb.AddJobHandleForProducer(Dependency);
    }
}